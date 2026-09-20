/**
 * `globalThis.fetch` 替身（本变更所有用例复用）。
 *
 * 能力：按 URL 路径路由 + REST JSON 响应 + 调用记录。
 * 用途：AG-UI 事件流（配合 `sse.ts`）、`GET /models`、`GET /products`、`/cart*` 的离线替身。
 *
 * 未匹配到任何路由时**显式抛错**（而不是回一个泛泛的 404）—— 让"忘了注册路由"立刻暴露；
 * 需要断言 404 分支的用例请显式注册一个返回 404 的路由。
 */

export interface FetchCall {
  /** 原始请求 URL 字符串。 */
  url: string
  /** 路径（不含 query）。 */
  path: string
  /** 查询串，含前导 `?`；无查询时为空串。 */
  search: string
  method: string
  headers: Record<string, string>
  /** JSON 解析后的请求体；无请求体或解析失败时为 `undefined`。 */
  body: unknown
  /** 原始请求体文本；无请求体时为空串。 */
  rawBody: string
}

export interface RouteContext {
  call: FetchCall
  params: URLSearchParams
  body: unknown
}

export type RouteHandler = (ctx: RouteContext) => Response | Promise<Response>

/** 可直接当响应体返回的 JSON 值。 */
export type JsonValue =
  | string
  | number
  | boolean
  | null
  | JsonValue[]
  | { [key: string]: JsonValue }

/**
 * 路由值：
 * - 函数 → 由它返回 `Response`（可自定义状态码 / SSE / 错误体）；
 * - JSON 值 → 序列化为 `200 application/json` 的响应体。
 */
export type RouteSpec = RouteHandler | JsonValue

export interface FetchStub {
  readonly calls: readonly FetchCall[]
  /** 按**路径**筛选已记录调用（精确匹配 pathname）。 */
  callsTo(path: string): readonly FetchCall[]
  restore(): void
}

const BASE = 'http://localhost'

function normalizeHeaders(headers: RequestInit['headers']): Record<string, string> {
  const result: Record<string, string> = {}
  if (!headers) return result

  if (headers instanceof Headers) {
    headers.forEach((value, key) => {
      result[key] = value
    })
    return result
  }

  if (Array.isArray(headers)) {
    for (const [key, value] of headers) result[key] = value
    return result
  }

  for (const [key, value] of Object.entries(headers)) {
    if (typeof value === 'string') result[key] = value
  }
  return result
}

function resolveRoute(
  routes: Map<string, RouteSpec>,
  path: string,
): { spec: RouteSpec | undefined } {
  const exact = routes.get(path)
  if (exact !== undefined) return { spec: exact }

  // 前缀匹配（按 key 长度降序）：支持 `/cart` 命中 `/cart/items/{id}` 这类子路径。
  const candidates = [...routes.keys()].sort((a, b) => b.length - a.length)
  for (const key of candidates) {
    if (path === key || path.startsWith(`${key}/`)) return { spec: routes.get(key) }
  }

  return { spec: undefined }
}

export function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

export function textResponse(body: string, status = 200): Response {
  return new Response(body, { status, headers: { 'Content-Type': 'text/plain' } })
}

/**
 * 让返回的 `Response` 与调用方的 `AbortSignal` 联动 —— 复刻真实 `fetch` 的语义：
 * **中止会打断流式读取**（body 的读取方收到 `AbortError`），而不是静默地把流当正常结束。
 *
 * 为什么替身需要它：`@ag-ui/client` 的 `HttpAgent` 把 `abortController.signal` 传给 fetch，
 * `abortRun()` 正是经它生效。替身若无视该 signal，中止就静默失效——依赖「中止能收尾」的
 * 用例（C2 的挂起兜底）会测不出来；更糟的是它会给出「中止成功」的假象。
 */
function linkAbort(response: Response, signal: AbortSignal): Response {
  if (response.body === null) return response

  const reader = response.body.getReader()
  let bodyController: ReadableStreamDefaultController<Uint8Array> | undefined

  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      bodyController = controller
    },
    async pull(controller) {
      try {
        const { done, value } = await reader.read()
        if (done) {
          controller.close()
          return
        }

        controller.enqueue(value)
      } catch (error) {
        controller.error(error)
      }
    },
    cancel(reason) {
      return reader.cancel(reason)
    },
  })

  const onAbort = (): void => {
    // 关键：用 error（而非 close）终结流——close 会被读方当作「正常收尾」，那样就不是中止了。
    bodyController?.error(new DOMException('The operation was aborted.', 'AbortError'))
    void reader.cancel().catch(() => {})
  }

  if (signal.aborted) onAbort()
  else signal.addEventListener('abort', onAbort, { once: true })

  return new Response(body, {
    status: response.status,
    statusText: response.statusText,
    headers: response.headers,
  })
}

export function installFetchStub(routes: Record<string, RouteSpec>): FetchStub {
  const table = new Map(Object.entries(routes))
  const original = globalThis.fetch
  const calls: FetchCall[] = []

  const stub = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const rawUrl =
      typeof input === 'string' ? input : input instanceof URL ? input.href : input.url
    const parsed = new URL(rawUrl, BASE)

    const rawBody = typeof init?.body === 'string' ? init.body : ''
    const call: FetchCall = {
      url: rawUrl,
      path: parsed.pathname,
      search: parsed.search,
      method: (init?.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase(),
      headers: normalizeHeaders(init?.headers),
      body: rawBody ? (JSON.parse(rawBody) as unknown) : undefined,
      rawBody,
    }
    calls.push(call)

    const { spec } = resolveRoute(table, parsed.pathname)
    if (spec === undefined) {
      throw new Error(`fetch-stub 无匹配路由：${call.method} ${call.path}`)
    }

    if (typeof spec === 'function') {
      const response = await (spec as RouteHandler)({
        call,
        params: parsed.searchParams,
        body: call.body,
      })
      return init?.signal ? linkAbort(response, init.signal) : response
    }

    return jsonResponse(spec)
  }

  globalThis.fetch = stub as typeof fetch

  return {
    calls,
    callsTo: (path: string) => calls.filter((call) => call.path === path),
    restore: () => {
      globalThis.fetch = original
    },
  }
}
