# IndexCloner

A simple dotnet core 3.1 utility to copy an Azure Search index from one search service to another. This tool is built using the [Version 11 of the Azure Cognitive Search libraries for .NET](https://docs.microsoft.com/en-us/dotnet/api/overview/azure/search?view=azure-dotnet)

Usage: `IndexCloner.exe <source-search-service-name> <source-search-service-key> <destination-search-service-name> <destination-search-service-key> <index-name> <filter-field> [copyIndexDefinition]`  
e.g. `IndexCloner.exe service-one AEF345349023CD service-two CED35734902EEF copy-me lastUpdate true`

## Caveats and limitations

* All the fields on the source need to be marked as retreivable, if they are not you will loose the data in those fields
* The filter-field must be both sortable and retrievable
* This tool currently provides no re-try logic for failed actions
* If there are changes to the data in the source during execution of this tool they may not be captured and migrated to the destination index
* I'm making this available for use for free, I provide no warranties express or implied, use at your own risk
* That said it worked just fine for my case.

## FAQ

* Why is a filter-field required?  

> The Azure Search service has a [hard limit of 100K for the $skip](https://docs.microsoft.com/en-us/rest/api/searchservice/search-documents#skip-optional)
parameter which is used in OData to page through results without a field to build ordered filtered queries against
this tool would be unable to handle indexes with > 100,000 records
