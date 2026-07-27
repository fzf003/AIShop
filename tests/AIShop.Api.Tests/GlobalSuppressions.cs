// Pre-existing SonarAnalyzer S8969 and S3358 warnings in test files.
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(
    "CodeQuality", "S8969:Remove this null-forgiving operator",
    Justification = "Pre-existing in unmodified test files",
    Scope = "module")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S3358", Justification = "Pre-existing in SeedMessages test helper")]
