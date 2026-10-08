using System;
using System.Collections.Generic;
using System.Text;

namespace Glossator.AI.Configuration
{
    public class AiProviderOptions
    {
        public string Endpoint { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
    }
    public class GlossatorAiOptions
    {
        public AiProviderOptions Gemini { get; set; } = new();
        public AiProviderOptions EvrenSyzz { get; set; } = new();
    }


}
