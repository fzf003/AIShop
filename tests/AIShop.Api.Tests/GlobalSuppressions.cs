<<<<<<< HEAD
// Pre-existing SonarAnalyzer S8969 warnings in test files:
// These null-forgiving operators existed before this change and preserve
// the original authors' intent. Suppressed at project level to avoid
// blocking CI while not modifying unrelated test files.
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(
    "CodeQuality", "S8969:Remove this null-forgiving operator",
    Justification = "Pre-existing in unmodified test files",
    Scope = "module")]
=======
// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("SonarAnalyzer.CSharp", "S8969", Justification = "Null-forgiving operator in test seed data and setup patterns")]
>>>>>>> fb6af5f (feat(sqlitechat): T2 - SqliteChatHistoryProvider 重写 Store/Provide 适配 chat_messages 表)
