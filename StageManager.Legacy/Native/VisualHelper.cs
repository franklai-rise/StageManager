// This file contains two methods taken from MahApps.Metro to be able to remove the NuGet dependency.

// Taken from
//   MahApps.Metro/src/MahApps.Metro/Controls/WinApiHelper.cs
//   MahApps.Metro/src/MahApps.Metro/Controls/TreeHelper.cs

// Original license header:
//
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Windows;
using System.Windows.Media;

namespace StageManager.Native
{
	public static class VisualHelper
	{
		/// <summary>
		/// This method is an alternative to WPF's <see cref="VisualTreeHelper.GetParent"/> method, which also supports content elements. Keep in mind that for content element, this method falls back to the logical tree of the element!
		/// </summary>
		/// <param name="child">The item to be processed.</param>
		/// <returns>The submitted item's parent, if available. Otherwise null.</returns>
		public static DependencyObject? GetParentObject(this DependencyObject? child)
		{
			if (child is null)
				return null;

			// handle content elements separately
			if (child is ContentElement contentElement)
			{
				DependencyObject parent = ContentOperations.GetParent(contentElement);
				if (parent is not null)
					return parent;

				return contentElement is FrameworkContentElement fce ? fce.Parent : null;
			}

			var childParent = VisualTreeHelper.GetParent(child);
			if (childParent is not null)
				return childParent;

			// also try searching for parent in framework elements (such as DockPanel, etc)
			if (child is FrameworkElement frameworkElement)
			{
				DependencyObject parent = frameworkElement.Parent;
				if (parent is not null)
					return parent;
			}

			return null;
		}
	}
}
