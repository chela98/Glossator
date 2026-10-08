using System;
using System.Collections.Generic;
using System.Text;

namespace Glossator.AI.Prompts
{
    public static class PromptLoader
    {
        public static async Task<string> LoadPromptAsync(string fileName, CancellationToken ct = default)
        {
            //önce bin çıktısına bak
            var path = Path.Combine(AppContext.BaseDirectory, "Prompts", fileName);
            //bulamazsa proje kök dizinine bak
            if (!File.Exists(path))
            {
                path = Path.Combine(Directory.GetCurrentDirectory(), "Prompts", fileName);
            }
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Prompt dosyası bulunamadı {path}");
            }
            return await File.ReadAllTextAsync(path, ct);
        }
    }
}
