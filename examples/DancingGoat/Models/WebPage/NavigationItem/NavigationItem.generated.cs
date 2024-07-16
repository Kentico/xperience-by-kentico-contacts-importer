//--------------------------------------------------------------------------------------------------
// <auto-generated>
//
//     This code was generated by code generator tool.
//
//     To customize the code use your own partial class. For more info about how to use and customize
//     the generated code see the documentation at https://docs.xperience.io/.
//
// </auto-generated>
//--------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using CMS.ContentEngine;
using CMS.Websites;

namespace DancingGoat.Models
{
	/// <summary>
	/// Represents a page of type <see cref="NavigationItem"/>.
	/// </summary>
	[RegisterContentTypeMapping(CONTENT_TYPE_NAME)]
	public partial class NavigationItem : IWebPageFieldsSource
	{
		/// <summary>
		/// Code name of the content type.
		/// </summary>
		public const string CONTENT_TYPE_NAME = "DancingGoat.NavigationItem";


		/// <summary>
		/// Represents system properties for a web page item.
		/// </summary>
		[SystemField]
		public WebPageFields SystemFields { get; set; }


		/// <summary>
		/// NavigationItemName.
		/// </summary>
		public string NavigationItemName { get; set; }


		/// <summary>
		/// NavigationItemLink.
		/// </summary>
		public IEnumerable<WebPageRelatedItem> NavigationItemLink { get; set; }
	}
}