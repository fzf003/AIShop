# MiMo 在 AG-UI 路径的工具调用失效 — 遗留问题清单

> 状态：**待裁决**。记录于 2026-09-11。**暂不修复**——当前先完成 `agui-session-prod`（会话快照收敛 + TTL 清理）变更，本条留待其收口后处理。
> 性质：模型 / 工具调用链路问题，与 `agui-session-prod` 无关，**不要混在同一变更里改**。

## P0：MiMo 的工具调用退化为纯文本，工具静默不执行

### 现象

把 MiMo 切进 AG-UI 路径后，助手回复里出现模型**原始的工具调用模板标记**：

```
<tool_call>
<function=search_product>
<parameter=keyword>花束</parameter>
</function>
</tool_call>
```

工具**没有真正执行**（无 `TOOL_CALL_START` / `TOOL_CALL_RESULT` 事件、购物车无变化），这段标记作为普通助手文本流到了前端。

### 根因（两层）

1. **线格式不兼容**：OpenAI 兼容协议下工具调用必须以结构化 `tool_calls` 字段返回；MiMo 吐的是它自身 chat template 的文本语法（`<function=name><parameter=k>v</parameter>`）。网关（`https://token-plan-cn.xiaomimimo.com/v1`）未将其解析回 `tool_calls`，于是原样落在 `content` 里。
2. **AG-UI 路径无兜底**：`AguiHost` 的 `AGUIShoppingAgent` 走 MAF **原生 function calling**（`AIFunction` 工具集），既没有文本路径兜底、也没有对这类标记的流式清洗，所以「模型输出了标记」＝「这一轮没有工具调用」＝标记直接外泄给用户。

### 关键证据

| 事实 | 出处 |
|------|------|
| `search_product` **是真实注册的工具**（`AGUIShopping` 8 工具之一，`CartToolProvider.CreateTools()`） | `src/AIShop.AguiHost/Agents/AGUIShoppingAgent.cs:17` |
| MiMo 在本次配置里是 `Models:gpt-4.1` 键 → endpoint `token-plan-cn.xiaomimimo.com/v1`、模型 `mimo-v2.5`、显示名 `Mimo`；且为当前 `ActiveModel` | `src/AIShop.AguiHost/appsettings.json:19-25` |
| **项目早已得出「这类模型工具调用不可靠」的结论**：老 `/api/chat` 路径刻意放弃原生 function calling，四模型统一走「Instructions 内嵌 JSON 示例 + 服务端 `IndexOf('{')` 抠 JSON」纯文本模式 | `src/AIShop.Service/ShoppingAssistantAgent.cs:49`（「测试报告证明这是唯一 4 模型（OpenAI/DeepSeek/Qwen/MiMo）100% 兼容的路径」） |
| 老路径的分叉说明：`- OpenAI (gpt-/o1-/o3-)：用 _isOpenAI 加强 ForJsonSchema 兜底`；`- 非 OpenAI（千问/DeepSeek/MiMo）：纯 Text 路径` | `src/AIShop.Service/ShoppingAssistantAgent.cs:232-234` |
| 对照：**qwen 在 AG-UI 上实测能正常出结构化工具调用**（SSE 含 `TOOL_CALL_START search_product` → `TOOL_CALL_RESULT 找到 N 个商品`） | `.claude/agent-memory/shared/glossary.md`（T6 / T16 实测记录） |

即：模型**选对了工具、参数也对**，坏的只是线格式；且这不是新问题——老路径正是为了绕开它才改成文本模式的。

### 待核实（未验证，别当成结论）

- DeepSeek / 其他非 OpenAI 模型在 AG-UI 路径是否同样失效（老路径把它们和 MiMo 并列在「纯 Text 路径」，但 qwen 在 AG-UI 上实测是正常的，说明「非 OpenAI」并非充分条件）。
- 网关侧是否存在开启工具调用解析的开关（托管端点大概率没有，需确认）。
- 是否只是流式（streaming + tools）场景下失效，非流式是否正常。

### 影响面

- **模型切换功能**（`agui-model-switch`）切到 MiMo 时，**工具调用类能力全部静默失效**，且失败方式对用户表现为「模型在说胡话」而非报错——**静默失败**是这里最不好的地方。
- 非工具类问答不受影响。

---

## 候选方案（三选一，未决）

| 方案 | 做法 | 成本 | 权衡 |
|------|------|------|------|
| **A. 限定可用模型（推荐起点）** | AG-UI 路径只暴露支持原生工具调用的模型（qwen / gpt 已验证），MiMo 留给老 `/api/chat` 文本路径；或在模型清单里标注能力位，切换 UI 据此禁用 | 低 | 成本最低、符合项目既有结论；代价是 MiMo 在 AG-UI 不可用 |
| **B. 流式兜底解析** | 在流式层把 `<tool_call>…</tool_call>` 解析为 `FunctionCallContent` 并驱动工具循环 | 高 | 真正让 MiMo 可用；难点在**流式增量**上做增量解析与回撤（标记可能跨 chunk 边界），且需处理「已流出文本再回撤」的 UI 语义。可参考老路径 `ReplySanitizer` + `ChatEndpoints.cs` 正则的流式增量清洗先例 |
| **C. 最小防护：清洗** | 即使不解析，也**不让这段标记泄漏给用户**——识别并剥离/替换，或提示「当前模型不支持工具调用」 | 中 | 不解决「工具不执行」，但消除最差体验（把静默失败变成显式告知）。可与 B 分步实施（先 C 后 B） |

**建议路线**：先 A 止血（明确哪些模型可用于 AG-UI），再视需要做 C（消除泄漏），B 作为独立变更评估。

## 验收方式（无论选哪条）

1. 用 MiMo 发一条必然触发工具的请求（如「帮我找花束」）：
   - 方案 A/C：**不出现**原始标记泄漏；A 还应表现为模型不可选/被拦。
   - 方案 B：SSE 出现 `TOOL_CALL_START search_product` + `TOOL_CALL_RESULT`，工具真实执行。
2. 对 qwen 回归：确认仍出结构化工具调用（防止兜底逻辑误伤正常路径）。
3. 单元/集成测试：mock 一个「返回模板文本而非 `tool_calls`」的 `IChatClient`，覆盖解析或清洗分支。
