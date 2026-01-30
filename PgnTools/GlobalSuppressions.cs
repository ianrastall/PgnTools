using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    "MVVMTK",
    "MVVMTK0045",
    Justification = "ViewModels rely on runtime MVVM patterns that are not AOT-focused.",
    Scope = "namespaceanddescendants",
    Target = "~N:PgnTools.ViewModels")]
