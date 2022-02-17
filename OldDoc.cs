using Newtonsoft.Json;

namespace IndexCloner
{
    public class OldDoc
    {
        public OldDoc(string _oldFieldOne)
        {
            oldFieldOne = _oldFieldOne;
        }

        [JsonConstructor]
        public OldDoc(
            string _oldFieldOne,
            string _oldFieldTwo
        )
        {
            oldFieldOne = _oldFieldOne;
            oldFieldTwo = _oldFieldTwo;
        }

        [JsonProperty("oldFieldOne")]
        public string oldFieldOne { get; set; }

        [JsonProperty("oldFieldTwo")]
        public string oldFieldTwo { get; set; }
    }
}