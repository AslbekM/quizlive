# QuizLive

A live quiz platform. The host runs a game from a laptop, players join from
their phones with a room code, and questions, answer counts and the leaderboard
push to everyone in real time over SignalR.

```
┌─────────────┐   WebSocket    ┌──────────────────┐
│ Host screen │◄──────────────►│                  │
└─────────────┘                │  QuizHub         │  transport only
┌─────────────┐                │    ↓             │
│ Player #1   │◄──────────────►│  GameService     │  every rule lives here
├─────────────┤                │    ↓             │
│ Player #n   │◄──────────────►│  SQLite (EF)     │
└─────────────┘                └──────────────────┘
```

## Running it

Requires .NET 10 and Node 20+.

```bash
# backend + the built client on http://localhost:5086
dotnet run --project src/QuizLive.Api

# client with hot reload on http://localhost:5173 (proxies /api and /hub)
cd client && npm install && npm run dev
```

Open the host view, then open the join link on a second device or browser
profile. The database is created and seeded with a sample quiz on first run.

```bash
dotnet test        # 30 tests
```

## The three problems worth talking about

Most of this app is ordinary CRUD. These three parts are not, and they are the
reason the project exists.

### 1. The server owns the clock

Scores depend on how fast you answer, so answer speed is a value worth forging.
`SubmitAnswer` therefore takes only the chosen option — there is deliberately
no parameter for elapsed time. The server stamps `QuestionStartedUtc` when it
releases a question and measures the gap itself:

```csharp
var elapsedMs = (int)(_clock.GetUtcNow() - session.QuestionStartedUtc.Value).TotalMilliseconds;
```

`TimeProvider` is injected rather than read statically, so the tests drive the
clock directly and assert on speed-based scoring without sleeping.

### 2. A double tap cannot score twice

Players tap buttons twice, and a client that reconnects mid-request may replay
the message it was unsure about. A check-then-insert loses that race, so the
guarantee is a unique index on `(SessionId, QuestionId, PlayerId)`.

The important detail is that the answer row and the score increment are saved
in **one** `SaveChangesAsync`. When the index rejects the duplicate, the points
roll back with it, which is what makes the operation genuinely idempotent
rather than merely usually-correct.

`AnswerConstraintTests` asserts the index directly instead of inferring it from
a concurrency test, because SQLite will happily serialise two "concurrent"
calls and let such a test pass without the constraint ever being consulted.

### 3. A refreshed phone lands where the game actually is

Players reload, lose signal, and lock their screens. Every client gets a full
`StateSnapshot` on join and on every reconnect, rather than replaying missed
messages. It carries the live question with its **real remaining time**, the
leaderboard, and what that player already did — the option they picked and the
points it earned.

Identity survives via a `ReconnectToken` issued at join, so reclaiming a seat
keeps the original score instead of minting a second player. SignalR assigns a
new connection id on every reconnect, so both roles re-join on `onreconnected`
to restore group membership; assuming it survived is a quiet way to stop
receiving broadcasts.

## Layout

```
src/QuizLive.Api/
  Domain/      entities + Scoring (pure, no clock, no database)
  Data/        DbContext and the constraints
  Services/    GameService — all state transitions
  Hubs/        QuizHub — transport, group fan-out
  Contracts/   DTOs shared with the client
client/        React + TypeScript + Vite
tests/         xUnit against real SQLite and a fake clock
```

`npm run build` emits into the API's `wwwroot`, so one `dotnet run` serves the
API and the client together.

## Known gaps

- Scaling past one server needs a Redis backplane; groups are per-process today.
- The host drives question transitions manually — there is no server-side timer
  that auto-reveals when the clock runs out.
- Anyone who knows a room code can open its host view; the host is not
  authenticated.
