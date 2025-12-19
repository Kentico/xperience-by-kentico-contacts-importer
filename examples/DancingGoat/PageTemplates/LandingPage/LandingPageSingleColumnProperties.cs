using Kentico.PageBuilder.Web.Mvc.PageTemplates;
using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Admin.Base.FormAnnotations;

namespace DancingGoat.PageTemplates
{
    public class LandingPageSingleColumnProperties : IPageTemplateProperties
    {
        /// <summary>
        /// Indicates if logo should be shown.
        /// </summary>
        [CheckBoxComponent(Label = "{$dancinggoat.landingpagesinglecolumn.showlogo.label$}", Order = 1)]
        public bool ShowLogo { get; set; } = true;


        /// <summary>
        /// Background color CSS class of the header.
        /// </summary>
        [RequiredValidationRule]
        [DropDownComponent(Label = "{$dancinggoat.landingpagesinglecolumn.headercolor.label$}", Order = 2,
            Options = "first-color;{$dancinggoat.landingpagesinglecolumn.headercolor.option.chocolate$}\nsecond-color;{$dancinggoat.landingpagesinglecolumn.headercolor.option.gold$}\nthird-color;{$dancinggoat.landingpagesinglecolumn.headercolor.option.espresso$}")]
#pragma warning disable KXE0004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        [ExcludeFromAiraTranslation]
#pragma warning restore KXE0004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        public string HeaderColorCssClass { get; set; } = "first-color";
    }
}
