import { useEffect, useRef, useState } from 'react'
import {
  createConnection,
  forgetIdentity,
  recallIdentity,
  rememberIdentity,
  type AnswerResult,
  type JoinResult,
  type QuestionView,
  type RevealView,
  type StateSnapshot,
} from '../api'
import { Countdown } from '../components/Countdown'

export function Play({ code }: { code: string }) {
  const remembered = recallIdentity(code)

  const [name, setName] = useState(remembered?.name ?? '')
  const [joined, setJoined] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [status, setStatus] = useState('idle')

  const [snapshot, setSnapshot] = useState<StateSnapshot | null>(null)
  const [question, setQuestion] = useState<QuestionView | null>(null)
  const [reveal, setReveal] = useState<RevealView | null>(null)
  const [picked, setPicked] = useState<number | null>(null)
  const [lastResult, setLastResult] = useState<AnswerResult | null>(null)

  const hub = useRef<ReturnType<typeof createConnection> | null>(null)
  const playerId = useRef<number | null>(remembered?.playerId ?? null)
  const token = useRef<string | null>(remembered?.token ?? null)

  const applySnapshot = (snap: StateSnapshot | null) => {
    if (!snap) return
    setSnapshot(snap)
    setQuestion(snap.question)
    setReveal(snap.reveal)
    // The server is the authority on what we already answered; trusting local
    // state here would let a refresh hand us a second go at a live question.
    setPicked(snap.you?.answeredOptionIndex ?? null)
    if (snap.you) {
      playerId.current = snap.you.playerId
      token.current = snap.you.reconnectToken
      rememberIdentity(code, {
        playerId: snap.you.playerId,
        token: snap.you.reconnectToken,
        name: snap.you.name,
      })
    }
  }

  const join = async (displayName: string) => {
    const connection = hub.current
    if (!connection) return

    const result = await connection.invoke<JoinResult>(
      'PlayerJoin',
      code,
      displayName,
      token.current,
    )

    if (!result.ok) {
      setError(result.error)
      // A token the server does not recognise is worse than none: drop it so
      // the next attempt is treated as a clean join.
      if (token.current) {
        forgetIdentity(code)
        token.current = null
        playerId.current = null
      }
      return
    }

    setError(null)
    setJoined(true)
    applySnapshot(result.snapshot)
  }

  useEffect(() => {
    const connection = createConnection()
    hub.current = connection

    connection.on('QuestionStarted', (q: QuestionView) => {
      setQuestion(q)
      setReveal(null)
      setPicked(null)
      setLastResult(null)
    })
    connection.on('Revealed', (r: RevealView) => {
      setReveal(r)
      setQuestion(null)
    })
    connection.on('GameOver', (snap: StateSnapshot) => {
      applySnapshot(snap)
      setQuestion(null)
    })

    connection.onreconnecting(() => setStatus('reconnecting'))
    connection.onreconnected(async () => {
      // Rejoin with the token: it re-adds this new connection to the room's
      // group and returns the authoritative state we missed while away.
      if (token.current) await join(name || remembered?.name || '')
      setStatus('live')
    })
    connection.onclose(() => setStatus('offline'))

    connection
      .start()
      .then(async () => {
        setStatus('live')
        if (token.current) await join(remembered?.name ?? '')
      })
      .catch(() => setStatus('offline'))

    return () => {
      connection.stop()
    }
    // Intentionally mounted once per room code.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [code])

  const answer = async (index: number) => {
    if (picked !== null) return
    setPicked(index)

    const result = await hub.current?.invoke<AnswerResult>(
      'SubmitAnswer',
      code,
      playerId.current,
      index,
    )

    if (result) {
      setLastResult(result)
      if (!result.accepted) setPicked(null)
    }
  }

  if (!joined) {
    return (
      <div className="shell narrow">
        <section className="card center">
          <div className="muted small">Room</div>
          <div className="code">{code}</div>
          <input
            className="field"
            placeholder="Your name"
            value={name}
            maxLength={24}
            onChange={(e) => setName(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && name.trim() && join(name)}
          />
          <button className="primary" disabled={!name.trim()} onClick={() => join(name)}>
            Join
          </button>
          {error && <p className="error">{error}</p>}
          <p className="muted small">
            <span className={`dot ${status === 'live' ? 'ok' : 'warn'}`} /> {status}
          </p>
        </section>
      </div>
    )
  }

  const finished = snapshot?.state === 'Finished'

  return (
    <div className="shell narrow">
      <header className="bar">
        <div>
          <div className="muted small">{snapshot?.you?.name}</div>
          <div className="code small-code">{code}</div>
        </div>
        <div className="right">
          <div className="muted small">Score</div>
          <div className="big">{lastResult?.totalScore ?? snapshot?.you?.score ?? 0}</div>
        </div>
      </header>

      {status !== 'live' && <p className="banner">Connection {status}…</p>}

      {!question && !reveal && !finished && (
        <section className="card center">
          <h2>You are in</h2>
          <p className="muted">Waiting for the host to start.</p>
        </section>
      )}

      {question && (
        <section className="card">
          <div className="muted small">
            Question {question.index + 1} of {question.totalQuestions}
          </div>
          <h2>{question.text}</h2>
          <Countdown msRemaining={question.msRemaining} />
          <div className="options">
            {question.options.map((option, i) => (
              <button
                key={i}
                className={`option tap ${picked === i ? 'chosen' : ''}`}
                disabled={picked !== null}
                onClick={() => answer(i)}
              >
                <span className="pill">{'ABCD'[i]}</span>
                {option}
              </button>
            ))}
          </div>
          {picked !== null && (
            <p className="muted">
              {lastResult?.wasDuplicate ? 'Already locked in.' : 'Answer locked in.'}
            </p>
          )}
          {lastResult && !lastResult.accepted && <p className="error">{lastResult.reason}</p>}
        </section>
      )}

      {reveal && !finished && (
        <section className="card center">
          {picked === null ? (
            <h2 className="dimmed">No answer</h2>
          ) : picked === reveal.correctIndex ? (
            <h2 className="good">Correct · +{lastResult?.pointsAwarded ?? snapshot?.you?.answeredPoints ?? 0}</h2>
          ) : (
            <h2 className="bad">Wrong</h2>
          )}
          <p className="muted">
            Answer: {'ABCD'[reveal.correctIndex]}
          </p>
          <ol className="board">
            {reveal.leaderboard.slice(0, 5).map((row, i) => (
              <li key={row.playerId} className={row.playerId === playerId.current ? 'lead' : ''}>
                <span className="rank">{i + 1}</span>
                <span className="name">{row.name}</span>
                <span className="score">{row.score}</span>
              </li>
            ))}
          </ol>
        </section>
      )}

      {finished && (
        <section className="card center">
          <h2>That is the end</h2>
          <ol className="board">
            {(snapshot?.leaderboard ?? []).map((row, i) => (
              <li key={row.playerId} className={row.playerId === playerId.current ? 'lead' : ''}>
                <span className="rank">{i + 1}</span>
                <span className="name">{row.name}</span>
                <span className="score">{row.score}</span>
              </li>
            ))}
          </ol>
        </section>
      )}
    </div>
  )
}
