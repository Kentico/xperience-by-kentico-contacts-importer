using Kentico.PageBuilder.Web.Mvc;
using Kentico.Xperience.Admin.Base;

namespace DancingGoat.Widgets
{
    /// <summary>
    /// Properties for Testimonial widget.
    /// </summary>
    public class TestimonialWidgetProperties : IWidgetProperties
    {
        /// <summary>
        /// Quotation text.
        /// </summary>
        public string QuotationText { get; set; }


        /// <summary>
        /// Author text.
        /// </summary>
        public string AuthorText { get; set; }


        /// <summary>
        /// Background color CSS class.
        /// </summary>
#pragma warning disable KXE0004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        [ExcludeFromAiraTranslation]
#pragma warning restore KXE0004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        public string ColorCssClass { get; set; } = "first-color";
    }
}
