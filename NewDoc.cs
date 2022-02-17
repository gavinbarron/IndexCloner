using Newtonsoft.Json;

namespace IndexCloner
{
    public class NewDoc
    {
        public NewDoc(string _newFieldOne)
        {
            newFieldOne = _newFieldOne;
        }

        [JsonConstructor]
        public NewDoc(
            string _newFieldOne,
            string _newFieldTwo,
            string _newFieldThree
        )
        {
            newFieldOne = _newFieldOne;
            newFieldTwo = _newFieldTwo;
            newFieldThree = _newFieldThree;
        }

        [JsonProperty("newFieldOne")]
        public string newFieldOne { get; set; }

        [JsonProperty("newFieldTwo")]
        public string newFieldTwo { get; set; }

        [JsonProperty("newFieldThree")]
        public string newFieldThree { get; set; }
    }
}