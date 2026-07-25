// Pre-existing SonarAnalyzer S8969 warnings in test files:
// These null-forgiving operators existed before this change and preserve
// the original authors' intent. Suppressed at project level to avoid
// blocking CI while not modifying unrelated test files.
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(
    "CodeQuality", "S8969:Remove this null-forgiving operator",
    Justification = "Pre-existing in unmodified test files",
    Scope = "module")]
