using System;
using System.Collections.Generic;
using System.Text;

namespace Glossator.Core.Models.Glossary
{
    public enum GlossingPolicy
    {
        /// <summary>
        /// İlk 2 geçişte hibrit (ingilizce + türkçe),3 ve sonrasında saf ingilizce
        /// </summary>
        ProgressiveGloss,
        /// <summary>
        /// Sektör jargonu: Asla Türkçeleştirilmeyenler  
        /// </summary>
        AlwaysEnglish,
        /// <summary>
        /// Düz kavramlar: Doğrudan Türkçesi kullanılır (Data privacy -> Veri gizliliği vb.).
        /// </summary>
        AlwaysTurkish

    }
}
