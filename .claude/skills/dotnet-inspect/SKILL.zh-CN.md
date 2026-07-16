---
name: dotnet-inspect
description: "查询 .NET API，支持 NuGet 包、平台库和本地文件。可搜索类型、列出 API 表面、比较不同版本、查找扩展方法和实现类。"
---

# dotnet-inspect

查询 .NET 库 API —— 相同的命令可同时用于 NuGet 包、平台库（System.*、Microsoft.AspNetCore.*）以及本地 .dll/.nupkg 文件。

## 快速决策树

- **代码报错了吗？** → 先执行 `diff --package Foo@old..new`，再查看 `member --oneline`
- **需要 API 表面？** → 使用 `member Type --package Foo --oneline`（更省 token）
- **需要签名信息？** → 使用 `member Type --package Foo -m Method`（默认会显示完整签名和文档）
- **需要源码/IL 信息？** → 使用 `member Type --package Foo -m Method -v:d`（会额外显示 Source、Lowered C#、IL）
- **需要构造函数？** → 使用 `member 'Type<T>' --package Foo -m .ctor`（使用 `<T>`，不要用 `<>`）
- **需要所有重载？** → 使用 `member Type --package Foo --select`（会显示 `Name:N` 索引）

## 何时使用这个 Skill

- **“这个包里有哪些类型？”** — `type` 用于发现类型（简洁输出），`find` 用于按模式搜索
- **“这个 API 的表面是什么？”** — `type` 用于发现，`member` 用于详细检查（默认打开文档）
- **“不同版本之间改了什么？”** — `diff` 会分类显示破坏性/增量性变化
- **“这段代码用了旧 API，需要修复”** — 先对比 old..new 版本，再用 `member --oneline` 看新 API
- **“这个类型有哪些扩展方法？”** — `extensions` 会查找扩展方法/属性
- **“这个接口有哪些实现类？”** — `implements` 会找到具体实现类型
- **“这个类型依赖了什么？”** — `depends` 会向上遍历类型依赖层级
- **“这个包有哪些版本/元数据？”** — `package` 和 `library` 用于检查元数据
- **“给我看点有意思的内容”** — `demo` 会运行预置展示查询

## 关键模式

把 `--oneline` 作为扫描时的默认选项 —— 它同时适用于 `type`、`member`、`find`、`diff` 和 `implements`：

```bash
dnx dotnet-inspect -y -- member JsonSerializer --package System.Text.Json --oneline  # 扫描成员
dnx dotnet-inspect -y -- type --package System.Text.Json --oneline                   # 扫描类型
dnx dotnet-inspect -y -- diff --package System.CommandLine@2.0.0-beta4.22272.1..2.0.3 --oneline  # 先做变更判断
```

使用 `--shape` 可以快速理解一个类型的继承链和表面结构：

```bash
dnx dotnet-inspect -y -- type 'HashSet<T>' --platform System.Collections --shape
```

当修复损坏的代码时，先用 `diff` 做初步判断，再查看具体类型的详细信息：

```bash
dnx dotnet-inspect -y -- diff --package System.CommandLine@2.0.0-beta4.22272.1..2.0.3 --oneline  # 看改了什么？
dnx dotnet-inspect -y -- diff -t Command --package System.CommandLine@2.0.0-beta4.22272.1..2.0.3  # 查看 Command 的详细变化
dnx dotnet-inspect -y -- member Command --package System.CommandLine@2.0.3 --oneline              # 看新的 API 表面
```

## 搜索范围

搜索命令（`find`、`extensions`、`implements`、`depends`）使用范围标志：

- **不加标志** — 平台框架 + Microsoft.Extensions.AI
- **`--platform`** — 所有平台框架
- **`--extensions`** — 预选的 Microsoft.Extensions.* 包
- **`--aspnetcore`** — 预选的 Microsoft.AspNetCore.* 包
- **`--package Foo`** — 指定 NuGet 包（可与范围标志组合）

`type`、`member`、`library`、`diff` 支持通过 `--platform <name>` 指定某个特定平台库。

## 命令参考

| Command | Purpose |
| ------- | ------- |
| `type` | **发现类型** — 输出简洁，默认不显示文档；可配合 `--shape` 看继承层级 |
| `member` | **查看成员** — 默认开启文档；支持点语法（`-m Type.Member`） |
| `find` | 按 glob 模式搜索类型，跨任意范围检索 |
| `diff` | 比较不同版本之间的 API 表面；会分类标注破坏性/增量性变化 |
| `extensions` | 查找某个类型的扩展方法/扩展属性 |
| `implements` | 查找实现某个接口或继承某个基类的具体类型 |
| `depends` | 向上遍历类型依赖层级（接口、基类） |
| `package` | 查看包元数据、文件、版本、依赖；可用 `search` 做 NuGet 发现 |
| `library` | 查看库元数据、符号、引用、依赖 |
| `demo` | 运行预置展示查询 —— 支持列出、执行或随机选择 |

## 输出限制

**不要把输出通过 `head`、`tail` 或 `Select-Object` 管道过滤。** 这个工具内置了行数限制，能保留头部信息和格式：

```bash
dnx dotnet-inspect -y -- member JsonSerializer --package System.Text.Json --oneline -10  # 取前 10 行
dnx dotnet-inspect -y -- find "*Logger*" -n 5                                            # 取前 5 行
dnx dotnet-inspect -y -- member JsonSerializer --package System.Text.Json -v:q -s Methods  # 只看指定 section
```

- **`-n N` 或 `-N`** — 限制行数，类似 `head`；会保留标题并干净截断
- **`-s Section`** — 只显示某个 section（支持 glob）；单独使用 `-s` 可列出可用 section
- **`-v:q`** — 静默模式，输出更紧凑的摘要

## 关键语法

- **泛型类型需要加引号**：`'Option<T>'`、`'IEnumerable<T>'`
- **泛型使用 `<T>`，不要用 `<>`** —— `"Option<>"` 会解析为抽象基类，而 `'Option<T>'` 会解析为具体泛型类型及其构造函数
- **`type` 用 `-t` 做类型过滤，`member` 用 `-m` 做成员过滤（不是 `--filter`）**
- **`member` 的点语法**：`-m JsonSerializer.Deserialize`
- **差异范围使用 `..`**：`--package System.Text.Json@9.0.0..10.0.0`
- **签名信息会包含 `params` 和默认值**
- **派生类型只显示自己的成员** —— 需要同时查询基类（例如 `RootCommand` 继承自 `Command`，其 `Add()` 和 `SetAction()` 也来自 `Command`）

## 安装

使用 `dnx`（类似 `npx`）。始终加上 `-y` 和 `--`，避免交互式提示：

```bash
dnx dotnet-inspect -y -- <command>
```

## 完整文档

想看完整语法、边界情况和参数兼容性矩阵：

```bash
dnx dotnet-inspect -y -- llmstxt
```
