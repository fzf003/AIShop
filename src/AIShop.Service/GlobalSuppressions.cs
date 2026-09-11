// Service 全局 SonarAnalyzer 压制（分层搬迁重构：AGUIShoppingAgent 自 AguiHost 迁入，spec 指定命名保留）
using System.Diagnostics.CodeAnalysis;

// S101：类名 AGUIShoppingAgent 首段 AGUI 为全大写缩写，类名由 agui-host spec/tasks 指定（AGUIShoppingAgent，
// T4/T5/T6 装配契约引用），不按 pascal 改名为 AguiShoppingAgent，故仅对本类型压制。
[assembly: SuppressMessage("SonarAnalyzer.CSharp", "S101", Justification = "Class name mandated by agui-host spec (AGUIShoppingAgent)", Scope = "type", Target = "~T:AIShop.Service.Agui.AGUIShoppingAgent")]
