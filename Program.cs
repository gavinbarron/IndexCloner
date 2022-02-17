using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;

namespace IndexCloner
{
    class Program
    {
        private static int _successfullyIndexed = 0;
        private static int _failCount = 0;

        public static JsonDocument JsonDocumentFromObject(object value, JsonSerializerOptions options = null)
        {
            if (value is string valueStr)
            {
                try { return JsonDocument.Parse(valueStr); }
                catch { }
            }

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, options);
            return JsonDocument.Parse(bytes);
        }

        public static JsonElement JsonElementFromObject(object value, JsonSerializerOptions options = null)
        {
            JsonElement result;
            using (JsonDocument doc = JsonDocumentFromObject(value, options))
            {
                result = doc.RootElement.Clone();
            }
            return result;
        }

        static JsonElement Map(JsonElement input)
        {
            // 1:1 cloning with no DTO shape changing:
            return input;

            // Mapping between two DTO's of different shape:
            // string inputText = input.GetRawText();
            // OldDoc oldDoc = JsonSerializer.Deserialize<OldDoc>(inputText, new JsonSerializerOptions
            // {
            //     PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            // });
            //
            // NewDoc newDoc = new NewDoc(
            //     oldDoc.oldFieldOne,
            //     oldDoc.oldFieldTwo,
            //     "Hard coded string because OldDoc didn't have an equivalent"
            // );
            // JsonElement elementToReturn = JsonElementFromObject(newDoc);
            // return elementToReturn;
        }

        static async Task Main(string[] args)
        {
            if (args.Length == 1 && IsHelpParam(args[0]))
            {
                Console.WriteLine(
                    "Help: usage IndexCloner.exe <source-search-service-name> <source-search-service-key> <destination-search-service-name> <destination-search-service-key> <index-name> <filter-field> [copyIndexDefinition]");
                Environment.Exit(0);
                return;
            }

            if (args.Length != 7 && args.Length != 8)
            {
                await Console.Error.WriteLineAsync(
                    "Error: usage IndexCloner.exe <source-search-service-name> <source-search-service-key> <destination-search-service-name> <destination-search-service-key> <index-name> <filter-field> [copyIndexDefinition]");
                Environment.Exit(-1);
                return;
            }

            const int maxBatchSize = 9500;
            const int maxRecordsForQuery = 90000;
            string sourceSearchService = $"https://{args[0]}.search.windows.net";
            string sourceKey = args[1];
            string destinationSearchService = $"https://{args[2]}.search.windows.net";
            string destinationKey = args[3];
            string indexToClone = args[4];
            string indexDestinationName = args[5];
            string filterField = args[6];

            bool copyIndexDefinition = false;
            if (args.Length == 8)
                bool.TryParse(args[7], out copyIndexDefinition);
            var sourceEndpoint = new Uri(sourceSearchService);
            var sourceCredentials = new AzureKeyCredential(sourceKey);
            var sourceIndexClient = new SearchIndexClient(sourceEndpoint, sourceCredentials);
            var sourceSearchClient = new SearchClient(sourceEndpoint, indexToClone, sourceCredentials);

            var destinationEndpoint = new Uri(destinationSearchService);
            var destinationCredential = new AzureKeyCredential(destinationKey);
            var destinationIndexClient = new SearchIndexClient(destinationEndpoint, destinationCredential);
            var destinationSearchClient = new SearchClient(destinationEndpoint, indexDestinationName, destinationCredential);
            var definitionCloneTimer = new Stopwatch();

            Console.WriteLine($"Migration started {DateTimeOffset.UtcNow:O}");
            // only copy definition if set to do so and the destination index doesn't exist
            if (copyIndexDefinition && !(await DestinationHasIndexAlready(destinationIndexClient, indexToClone)))
            {
                await CloneIndexDefinition(
                    definitionCloneTimer,
                    sourceIndexClient,
                    indexToClone,
                    destinationIndexClient,
                    destinationSearchService
                );
            }

            Console.WriteLine($"Commencing data migration for {indexToClone} to {destinationSearchService}");
            var dataMigrationTimer = new Stopwatch();
            dataMigrationTimer.Start();
            var indexBatch = new IndexDocumentsBatch<JsonElement>();
            int totalRecordsInQuery = 0;
            bool allDone = false;
            string filter = "";
            // Compose the initial query
            Response<SearchResults<JsonElement>> searchAsync = await sourceSearchClient.SearchAsync<JsonElement>("*",
                                                                   new SearchOptions() { OrderBy = { filterField } });
            while (!allDone)
            {
                IAsyncEnumerable<Page<SearchResult<JsonElement>>> pages = searchAsync.Value.GetResultsAsync()
                                                                                     .AsPages();

                await foreach (Page<SearchResult<JsonElement>> page in pages)
                {
                    IndexDocumentsBatch<JsonElement> batch = indexBatch;
                    page.Values.ToList()
                        .ForEach(v =>
                             batch.Actions.Add(new IndexDocumentsAction<JsonElement>(
                                 IndexActionType.MergeOrUpload,
                                 Map(v.Document)
                             ))
                        );
#if !DEBUG
                    Console.Write(".");
#endif
                    totalRecordsInQuery += page.Values.Count;
#if DEBUG
                    Console.WriteLine($"Batch size: {indexBatch.Actions.Count}");
                    Console.WriteLine($"Records in query: {totalRecordsInQuery}");
#endif

                    // break conditions:
                    // There are no more records to fetch at all, exit the while loop
                    allDone = string.IsNullOrEmpty(page.ContinuationToken);
                    // We're approaching the 100K limit of $top, so break from foreach loop and build a new query
                    if (totalRecordsInQuery >= maxRecordsForQuery)
                        break;

                    // We still have capacity left in this batch to send to the destination
                    // Move to the next iteration and keep filling the batch
                    if (indexBatch.Actions.Count < maxBatchSize)
                        continue;

                    // Send the batch to the destination index
                    await SendDocumentsToDestination(destinationSearchClient, indexBatch);

                    // reset the count.
                    indexBatch = new IndexDocumentsBatch<JsonElement>();
                }

                // Ensure that any outstanding actions are sent before building a new query
                if (indexBatch.Actions.Any())
                {
                    await SendDocumentsToDestination(destinationSearchClient, indexBatch);
                }

                // Don't build a new query if we're all done, just exit the loop
                if (allDone)
                    continue;

                Console.WriteLine("");
                Console.WriteLine($"Query {filter} indexed");
                Console.WriteLine("++++++++++++++++++++=");
                // compose a new query for the next top level iteration
                var lastFilterValue = new JsonElement();
                if (indexBatch.Actions.LastOrDefault()
                             ?.Document.TryGetProperty(filterField, out lastFilterValue) ?? false)
                {
                    if (bool.TryParse(lastFilterValue.ToString(), out _) || DateTime.TryParse(lastFilterValue.ToString(), out _))
                    {
                        filter = $"{filterField} ge {lastFilterValue}";
                    }
                    else
                    {
                        filter = $"{filterField} ge '{lastFilterValue.ToString()}'";
                    }
                    searchAsync = await sourceSearchClient.SearchAsync<JsonElement>("*",
                                      new SearchOptions()
                                      {
                                          Filter = filter,
                                          OrderBy = { filterField }
                                      });
                }

                // reset outer loop counter
                totalRecordsInQuery = 0;
            }

            // report on timing and migration stats
            dataMigrationTimer.Stop();

            Console.WriteLine($"Migrated {_successfullyIndexed} successfully, {_failCount} failed");
            Console.WriteLine($"Migration took {dataMigrationTimer.Elapsed.TotalSeconds}s");
            Console.WriteLine($"Migration completed {DateTimeOffset.UtcNow:O}");
        }

        private static bool IsHelpParam(string paramValue) => paramValue == "/?" || paramValue == "/h" ||
                                                              paramValue == "--help" || paramValue == "help" ||
                                                              paramValue == "-H" || paramValue == "-h";

        private static async Task<bool> DestinationHasIndexAlready(
            SearchIndexClient destinationIndexClient,
            string indexToClone
        )
        {
            IAsyncEnumerable<Page<string>> indexNamePages = destinationIndexClient.GetIndexNamesAsync()
                                                                                  .AsPages();

            await foreach (Page<string> page in indexNamePages)
            {
                if (page.Values.Contains(indexToClone))
                    return true;
            }

            return false;
        }

        private static async Task SendDocumentsToDestination(
            SearchClient destinationSearchClient,
            IndexDocumentsBatch<JsonElement> indexBatch
        )
        {
            Response<IndexDocumentsResult> indexDocumentsAsync =
                await destinationSearchClient.IndexDocumentsAsync(indexBatch);
            // Report on failures.
            // A retry could potentially be added here.
            List<string> failedKeys = indexDocumentsAsync.Value.Results.Where(r => !r.Succeeded)
                                                         .Select(f => f.Key)
                                                         .ToList();
            failedKeys.ForEach(f => Console.WriteLine($"Failed to write document with key value {f}"));
            _failCount += failedKeys.Count;
            _successfullyIndexed += indexBatch.Actions.Count - failedKeys.Count;
            Console.WriteLine("");
            Console.WriteLine($"Batch indexed");
            Console.WriteLine($"==============");
        }

        private static async Task CloneIndexDefinition(
            Stopwatch definitionCloneTimer,
            SearchIndexClient sourceIndexClient,
            string indexToClone,
            SearchIndexClient destinationIndexClient,
            string destinationSearchService
        )
        {
            definitionCloneTimer.Start();
            Response<SearchIndex> index = await sourceIndexClient.GetIndexAsync(indexToClone);
            // using classic is not allowed?
            index.Value.Similarity = new BM25Similarity();

            Console.WriteLine($"Retrieved index {indexToClone}");
            await destinationIndexClient.CreateOrUpdateIndexAsync(index.Value, true);
            definitionCloneTimer.Stop();
            Console.WriteLine(
                $"Wrote index definition for {indexToClone} to {destinationSearchService} in {definitionCloneTimer.ElapsedMilliseconds}ms");
        }
    }
}
