import type { EngineInterface, Register } from 'claude-code'

// TokenOverlay's partner inside each Claude Code session. The overlay cannot reach a session, so the two meet in files
// under %APPDATA%\TokenOverlay\cache-bridge:
//   <session>.ping  the overlay writes a request id there when the person presses 핑 for this session
//   <session>.keep  the overlay writes {"until": ms} there while 유지 is on for this session (8 hours at most)
//   <session>.compact  the overlay writes a request id there when the person presses 압축 for this session, or
//                      `retry-<id>` when the session's log shows no compaction a few minutes after this mod tried one
//   <session>.json  this mod writes what it knows: alive, last main request, context, the last ping, a compaction,
//                   and its last compaction attempt (when, why, which way, what the call answered); it reads this
//                   back when the session's process starts
// A ping is a tool-less fork over the session's own transcript: it reads the context from the cache, which keeps the
// cache an hour longer, and leaves nothing in the conversation. With no ping, a large uncompacted session is
// compacted 5 minutes before its one-hour cache lapses, while the summary can still be read from the cache.
// While 유지 is on the mod pings by itself 10 minutes before the lapse, and once more 4 minutes later if that failed;
// the compaction stays the last resort should both fail.
// A main turn that ends answered with a large context is compacted then (TokenOverlay decides when), where nothing is in
// flight: 68% of the engine's own compactions came in the middle of a turn (2026-11-09 study, KB X-3127/X-3128).

const TICK_MS = 30_000
const TTL_MS = 60 * 60_000
const COMPACT_AT_MS = 55 * 60_000
const KEEP_AT_MS = 50 * 60_000
const KEEP_RETRY_AT_MS = 54 * 60_000
// The same size TokenOverlay warns at (CacheWatch.LargeContext).
const LARGE_CONTEXT = 150_000
// As long as the overlay still takes a report as alive (CacheWatch.PartnerFresh): it asks only a session that reports.
const REQUEST_WAITS_MS = 2 * 60_000
// Turn-end compaction (350k since 2026-11-10, first 400k from X-3127) is TokenOverlay's to decide (CacheWatch.TurnEnds): it asks through
// <session>.compact with a `turnend-` id. Builds 8 and 9 compacted 1 second after each answer by themselves; the user
// found that a message sent meanwhile waited behind it, and that a session still waiting on background work was
// compacted (2026-11-10), so the overlay now waits for quiet and for that work, and changes without a restart.
const TURN_END_PREFIX = 'turnend-'
// The same margin as CacheWatch.TurnEndStaleAfter: a turn's last request comes just before its end is asked.
const STALE_AFTER_MS = 2_000
const PING_PROMPT = '캐시 유지용 확인임. 도구를 쓰지 말고 ok 한 단어로만 답할 것.'
const COMPACT_INSTRUCTIONS = '자리를 비운 동안 캐시가 식기 전에 하는 압축임. 목표, 진행 상황, 바꾼 파일, 정해진 것과 남은 결정, 다음 할 일을 남길 것.'
const ASKED_INSTRUCTIONS = '사용자가 오버레이의 압축 버튼으로 요청한 압축임. 목표, 진행 상황, 바꾼 파일, 정해진 것과 남은 결정, 다음 할 일을 남길 것.'
const TURN_END_INSTRUCTIONS = '턴이 끝난 안전 지점에서 하는 압축임. 목표, 진행 상황, 바꾼 파일, 정해진 것과 남은 결정, 다음 할 일을 남길 것.'

type Ping = { request: string; at: number; ok: boolean; read?: number; write?: number; reason?: string; auto?: boolean }
type Compact = { request: string; at: number; ok: boolean; reason?: string }
// api: $.session.compact; command: /compact run as if typed. A call that answers well has not always compacted
// (2026-10-29, a session whose log never got the compaction), so the overlay checks the log and may ask a retry.
type Via = 'api' | 'command'
type Attempt = { at: number; by: 'auto' | 'asked' | 'retry' | 'turnend'; done?: number; via?: Via; ok?: boolean; reason?: string }
// autoTried: the idle stretch (its lastAt) the automatic compaction was tried in. Kept apart from `compacted`, which a
// failed asked or retried compaction clears: on 2026-11-07 that let the automatic one run again 30 seconds later, five
// times a stretch, in a session where both ways were refused. turnEndTried: the same for the turn-end compaction, by
// the last request of the turn it followed.
type Kept = {
  lastAt: number; context: number; compacted: boolean; handled: string; ping?: Ping; compactedAt?: number
  compactHandled?: string; compact?: Compact; attempt?: Attempt; autoTried?: number; turnEndTried?: number
}

let sessionId = ''
let folder = ''
let busy = false
let working = false
let kept: Kept = { lastAt: 0, context: 0, compacted: false, handled: '' }
// The compaction window this session's process settled at its start; /autocompact elsewhere does not move it,
// so TokenOverlay's ring measures against this rather than against settings.json.
let measured: { window?: number; threshold?: number } = {}
// 유지: until when the overlay asked (0 when off), and the pings tried in the current idle stretch.
let keepUntil = 0
let tries = { stretch: 0, count: 0 }
// The claude.exe this session runs in (0 until known), so the overlay can name the session that started a busy
// program: a session the desktop app starts fresh has no id on its command line (2026-11-09).
let pid = 0

// The parent of a process run without a shell is this session's own process.
async function findPid($: EngineInterface) {
  try {
    const result = await $.process.run(['powershell.exe', '-NoProfile', '-NonInteractive', '-Command',
      '(Get-CimInstance Win32_Process -Filter "ProcessId=$PID").ParentProcessId'], { timeoutMs: 30_000 })
    const found = Number(result.stdout.trim())
    if (result.exitCode === 0 && Number.isInteger(found) && found > 0) pid = found
  } catch (error) {
    $.ui.log(`tokenoverlay-cache: 세션 프로세스 번호를 못 찾음 (${String(error)})`)
  }
}

async function keep($: EngineInterface) {
  if (!sessionId || !folder) return
  // code: which build of this mod a session runs (2: compaction falls back to /compact, 3: 유지, 4: 압축 on request,
  // 5: attempts reported and retried the other way, 6: state read back from the report, 7: one automatic compaction
  // per idle stretch, a stopped session's refusal reported as such, 8: turn-end compaction at 400k, busy kept by the
  // main loop alone, 9: the session's process id, 10: turn-end compaction asked by the overlay alone, 11: its
  // instructions without the size, which the overlay now sets, 12: a turn-end request older than the session's last
  // request or compaction is dropped, not run, 13: a compaction it asked for that another overtook is vetoed), since edits
  // reach a running session only on a restart.
  const report = {
    v: 1, code: 13, sessionId, aliveAt: await $.clock.now(), busy, ...kept, ...measured, ...(keepUntil ? { keepUntil } : {}), ...(pid ? { pid } : {}),
  }
  await $.fs.write(`${folder}\\${sessionId}.json`, JSON.stringify(report))
}

// What the session's last process knew, from its own report and not from $.store: one store file shared by all
// sessions, zeroed when the PC went down (2026-10-31), made every session's start throw before its tick was set up.
async function restore($: EngineInterface) {
  const file = `${folder}\\${sessionId}.json`
  if (!folder || !(await $.fs.exists(file))) return
  const r = JSON.parse(await $.fs.read(file))
  if (typeof r.lastAt !== 'number') return
  kept = {
    lastAt: r.lastAt, context: r.context ?? 0, compacted: r.compacted === true, handled: r.handled ?? '', ping: r.ping,
    compactedAt: r.compactedAt, compactHandled: r.compactHandled, compact: r.compact, attempt: r.attempt,
    autoTried: typeof r.autoTried === 'number' ? r.autoTried : undefined,
    turnEndTried: typeof r.turnEndTried === 'number' ? r.turnEndTried : undefined,
  }
}

// A request older than a process is not waiting for it. It counts as handled, so a lost report cannot run it again.
async function oldRequest($: EngineInterface, file: string, now: number) {
  if (!(await $.fs.exists(file)) || now - (await $.fs.stat(file)).mtimeMs < REQUEST_WAITS_MS) return undefined
  return (await $.fs.read(file)).trim()
}

async function settleRequests($: EngineInterface) {
  if (!folder) return
  const now = await $.clock.now()
  kept.handled = (await oldRequest($, `${folder}\\${sessionId}.ping`, now)) ?? kept.handled
  kept.compactHandled = (await oldRequest($, `${folder}\\${sessionId}.compact`, now)) ?? kept.compactHandled
}

// A session the mod joined after its last request (installed or reloaded while it sat idle): the context from the
// status line's figures, the time from its transcript's last write, which follows the last request by seconds.
async function seed($: EngineInterface) {
  if (kept.lastAt) return
  const tokens = (await $.session.usage()).context.tokens ?? 0
  if (!tokens) return
  const config = (await $.env.get('CLAUDE_CONFIG_DIR')) || `${(await $.env.get('USERPROFILE')) || (await $.env.get('HOME')) || ''}\\.claude`
  const projects = `${config}\\projects`
  if (!(await $.fs.exists(projects))) return
  for (const dir of await $.fs.list(projects)) {
    if (dir.kind !== 'dir') continue
    const log = `${projects}\\${dir.name}\\${sessionId}.jsonl`
    if (!(await $.fs.exists(log))) continue
    kept.lastAt = (await $.fs.stat(log)).mtimeMs
    kept.context = tokens
    return
  }
}

async function measure($: EngineInterface) {
  try {
    const breakdown = (await $.session.usage({ breakdown: 'summary' })).context.breakdown
    if (breakdown) measured = { window: breakdown.rawMaxTokens, threshold: breakdown.autoCompactThreshold }
  } catch (error) {
    $.ui.log(`tokenoverlay-cache: 압축 기준을 못 읽음 (${String(error)})`)
  }
}

async function ping($: EngineInterface, request: string, now: number): Promise<Ping> {
  if (busy) return { request, at: now, ok: false, reason: 'busy' }
  if (!kept.lastAt || now - kept.lastAt >= TTL_MS) return { request, at: now, ok: false, reason: 'cold' }
  const reply = await $.model.fork({ prompt: PING_PROMPT })
  if (!reply.isAnswered && reply.reason === 'nothing-to-fork') return { request, at: now, ok: false, reason: 'nothing-to-fork' }
  const usage = 'usage' in reply ? reply.usage : undefined
  const read = usage?.cache_read_input_tokens ?? 0
  const write = usage?.cache_creation_input_tokens ?? 0
  // Read from the cache, and wrote little: the cache was warm and now lives another hour.
  const ok = reply.isAnswered && read > 0 && write * 10 < read
  if (ok) kept.lastAt = await $.clock.now()
  return { request, at: now, ok, read, write, reason: ok ? undefined : reply.isAnswered ? 'cache-missed' : reply.reason }
}

// Compacts the session now, and reports the attempt before and after (a long summary keeps the report from being
// rewritten meanwhile). A session the desktop app hosts runs headless, where $.session.compact has rejected: /compact
// is run as if typed instead. `avoid`: the way a retry must not take again. Whether it compacted is the log's to say.
async function compact($: EngineInterface, instructions: string, now: number, by: Attempt['by'], avoid?: Via) {
  kept.attempt = { at: now, by }
  await keep($)
  const result = await run($, instructions, avoid)
  kept.attempt = { ...kept.attempt, ...result, done: await $.clock.now() }
  return result
}

async function run($: EngineInterface, instructions: string, avoid?: Via): Promise<{ via: Via; ok: boolean; reason?: string }> {
  let refused = ''
  if (avoid !== 'api') {
    try {
      const result = await $.session.compact({ instructions })
      if ('skip' in result) {
        $.ui.log(`tokenoverlay-cache: 압축 건너뜀 (${result.skip})`)
        return { via: 'api', ok: false, reason: 'skipped' }
      }
      return { via: 'api', ok: true }
    } catch (error) {
      refused = String(error)
      if (avoid === 'command') {
        $.ui.log(`tokenoverlay-cache: 압축 못 함 (${refused})`)
        return { via: 'api', ok: false, reason: 'failed' }
      }
    }
  }
  try {
    await $.command.run({ command: 'compact', args: instructions })
    return { via: 'command', ok: true }
  } catch (fallback) {
    // A session another session stopped (stop_session) refuses /compact until the person types in it (seen 2026-11-07:
    // "the session was stopped and nobody has sent input since"); no other way is open, so the overlay does not retry.
    if (/session was stopped/i.test(String(fallback))) {
      $.ui.log('tokenoverlay-cache: 중지된 세션이라 압축 못 함 (이 세션에 직접 입력하면 다시 됨)')
      return { via: 'command', ok: false, reason: 'stopped' }
    }
    $.ui.log(`tokenoverlay-cache: 압축 못 함 (${refused} / ${String(fallback)})`)
    return { via: 'command', ok: false, reason: 'failed' }
  }
}

// A turn-end request is for the turn end it was asked at (TokenOverlay's CacheWatch.WithdrawStale). One this session
// made a request after (the person wrote before this mod's next look, so it waited a whole turn) or was compacted
// after (the engine's own compaction in that turn) is dropped: on 2026-11-13 one ran at 116k right after the engine had
// compacted at 367k. The overlay weighs the next turn end anew.
async function staleTurnEnd($: EngineInterface, file: string) {
  const askedAt = (await $.fs.stat(file)).mtimeMs
  return kept.lastAt > askedAt + STALE_AFTER_MS || (kept.compactedAt ?? 0) > askedAt
}

// A main-loop compaction this mod asked for (known by its instructions), coming after a compaction with no main
// request since, or after one that came after this mod's last attempt began: there is nothing left to compact.
const OWN_INSTRUCTIONS = [COMPACT_INSTRUCTIONS, ASKED_INSTRUCTIONS, TURN_END_INSTRUCTIONS]
function overtaken(e: { agentId?: string; instructions?: string }) {
  if (e.agentId || !OWN_INSTRUCTIONS.includes((e.instructions ?? '').trim()) || !kept.compactedAt) return false
  return kept.compactedAt > kept.lastAt || (kept.attempt !== undefined && kept.compactedAt > kept.attempt.at)
}

async function readKeep($: EngineInterface, now: number): Promise<number> {
  const file = `${folder}\\${sessionId}.keep`
  if (!(await $.fs.exists(file))) return 0
  try {
    const until = Number(JSON.parse(await $.fs.read(file)).until)
    return Number.isFinite(until) && until > now ? until : 0
  } catch {
    return 0
  }
}

async function tick($: EngineInterface) {
  if (working || !sessionId || !folder) return
  working = true
  try {
    const now = await $.clock.now()
    const request = `${folder}\\${sessionId}.ping`
    if (await $.fs.exists(request)) {
      const id = (await $.fs.read(request)).trim()
      if (id && id !== kept.handled) {
        kept.handled = id
        kept.ping = await ping($, id, now)
      }
    }
    // 압축 asked from the overlay (the person's button, or its turn-end rule): run once the session is not answering,
    // whatever its size or cache. A retry the overlay asks takes the other way than the attempt it follows.
    const compactAsk = `${folder}\\${sessionId}.compact`
    if (!busy && (await $.fs.exists(compactAsk))) {
      const id = (await $.fs.read(compactAsk)).trim()
      const turnEnd = id.startsWith(TURN_END_PREFIX)
      if (id && id !== kept.compactHandled && turnEnd && (await staleTurnEnd($, compactAsk))) {
        kept.compactHandled = id
      } else if (id && id !== kept.compactHandled) {
        kept.compactHandled = id
        kept.compacted = true
        const retry = id.startsWith('retry-')
        const instructions = turnEnd ? TURN_END_INSTRUCTIONS : !retry ? ASKED_INSTRUCTIONS
          : kept.attempt?.by === 'auto' ? COMPACT_INSTRUCTIONS : kept.attempt?.by === 'turnend' ? TURN_END_INSTRUCTIONS : ASKED_INSTRUCTIONS
        const by: Attempt['by'] = turnEnd ? 'turnend' : retry ? 'retry' : 'asked'
        const result = await compact($, instructions, now, by, retry ? kept.attempt?.via : undefined)
        kept.compact = { request: id, at: now, ok: result.ok, reason: result.reason }
        if (!result.ok) kept.compacted = false
      }
    }
    keepUntil = await readKeep($, now)
    if (keepUntil && !busy && !kept.compacted && kept.lastAt && now - kept.lastAt < TTL_MS - TICK_MS) {
      if (tries.stretch !== kept.lastAt) tries = { stretch: kept.lastAt, count: 0 }
      const due = tries.count === 0 ? KEEP_AT_MS : tries.count === 1 ? KEEP_RETRY_AT_MS : Infinity
      if (now - kept.lastAt >= due) {
        tries.count++
        kept.ping = { ...(await ping($, `keep-${now}`, now)), auto: true }
      }
    }
    // After a ping that kept the cache the stretch starts again, so this reads the time anew.
    const idle = (await $.clock.now()) - kept.lastAt
    if (!busy && !kept.compacted && kept.lastAt && kept.autoTried !== kept.lastAt && kept.context >= LARGE_CONTEXT
      && idle >= COMPACT_AT_MS && idle < TTL_MS - TICK_MS) {
      // Tried once per idle stretch: a vetoed or failed compaction is not retried until the next request.
      const stretch = kept.lastAt
      kept.autoTried = stretch
      kept.compacted = true
      await compact($, COMPACT_INSTRUCTIONS, now, 'auto')
      if (kept.lastAt !== stretch) kept.compacted = false
    }
    await keep($)
  } finally {
    working = false
  }
}

export const register: Register = on => {
  on('session.start', async ($, e, next) => {
    const result = await next(e)
    sessionId = await $.session.id()
    const appData = await $.env.get('APPDATA')
    folder = appData ? `${appData}\\TokenOverlay\\cache-bridge` : ''
    try { await restore($) } catch (error) { $.ui.log(`tokenoverlay-cache: 지난 보고를 못 읽음 (${String(error)})`) }
    try { await settleRequests($) } catch (error) { $.ui.log(`tokenoverlay-cache: 지난 요청을 못 읽음 (${String(error)})`) }
    await measure($)
    try { await seed($) } catch (error) { $.ui.log(`tokenoverlay-cache: 마지막 요청 시각을 못 찾음 (${String(error)})`) }
    $.clock.every(TICK_MS, () => { void tick($) })
    await keep($)
    void findPid($).then(() => keep($))
    return result
  })

  // The main loop's turns alone: a subagent's run ending inside a main turn must not mark the session idle.
  on('turn.start', ($, e, next) => {
    if (!e.agentId) busy = true
    return next(e)
  })

  on('turn.complete', async ($, e, next) => {
    const result = await next(e)
    if (e.agentId) return result
    busy = false
    await measure($)
    await keep($)
    return result
  })

  // Each main-loop request: when it was made and how large its context was (all input, cached or not).
  on('turn.step', async function* ($, e, next) {
    const result = yield* next(e)
    if (!e.agentId && result.usage) {
      const u = result.usage
      kept.lastAt = await $.clock.now()
      kept.context = u.input_tokens + u.cache_read_input_tokens + u.cache_creation_input_tokens
      kept.compacted = false
    }
    return result
  })

  // A compaction by anyone else (/compact, the engine's own): the old context size no longer applies.
  // One this mod asked for that comes after another compaction with nothing new since is vetoed (build 13): a /compact
  // run as if typed waits in the input queue behind a turn under way, and in that wait the person's own /compact, the
  // engine's, or the overlay's retry of the same request could compact first, so the queued one summarized the summary
  // (2026-11-13 17:52:41 255k then 17:53:18 23k, six such pairs since 11-10; the user's "자동압축뒤에 또 압축").
  on('session.compact', async ($, e, next) => {
    let vetoed = false
    try { vetoed = overtaken(e) } catch (error) { $.ui.log(`tokenoverlay-cache: 압축 중복 확인 못 함 (${String(error)})`) }
    if (vetoed) {
      $.ui.log('tokenoverlay-cache: 방금 압축돼서 이 압축은 건너뜀')
      return { skip: '방금 압축됨 (토큰오버레이 요청 건너뜀)' }
    }
    const result = await next(e)
    if (!e.agentId && !('skip' in result)) {
      kept.compacted = true
      kept.context = 0
      kept.compactedAt = await $.clock.now()
      await keep($)
    }
    return result
  })
}
