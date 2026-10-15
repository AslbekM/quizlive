import { useEffect, useRef, useState } from 'react'
import {
  createConnection,
  type QuestionView,
  type RevealView,
  type StateSnapshot,
} from '../api'
import { Countdown } from '../components/Countdown'

export function Host({ code }: { code: string }) {
  const [snapshot, setSnapshot] = useState<StateSnapshot | null>(null)
  const [question, setQuestion] = useState<QuestionView | null>(null)
  const [reveal, setReveal] = useState<RevealView | null>(null)
  const [answersIn, setAnswersIn] = useState(0)
  const [status, setStatus] = useState('connecting')
  const [busy, setBusy] = useState(false)
  const hub = useRef<ReturnType<typeof createConnection> | null>(null)

  useEffect(() => {
    const connection = createConnection()
    hub.current = connection
    let cancelled = false

    const apply = (snap: StateSnapshot | null) => {
      if (!snap || cancelled) return
      setSnapshot(snap)
      setQuestion(snap.question)
      setReveal(snap.reveal)
      setAnswersIn(snap.answersIn)
    }

    connection.on('LobbyUpdated', (_rows, count: number) =>
      setSnapshot((s) => (s ? { ...s, playerCount: count } : s)),
    )
    connection.on('AnswersProgress', (answered: number) => setAnswersIn(answered))
    connection.on('Revealed', (r: RevealView) => {
      setReveal(r)
      setQuestion(null)
    })
    connection.on('GameOver', (snap: StateSnapshot) => {
      apply(snap)
      setQuestion(null)
    })

    connection.onreconnecting(() => setStatus('reconnecting'))
    connection.onreconnected(async () => {
      // New connection id means the old group membership is gone; re-joining
      // restores it and resyncs whatever was missed.
      apply(await connection.invoke<StateSnapshot | null>('HostJoin', code))
      setStatus('live')
    })
    connection.onclose(() => setStatus('offline'))

    connection
      .start()
      .then(() => connection.invoke<StateSnapshot | null>('HostJoin', code))
      .then((snap) => {
        apply(snap)
        setStatus(snap ? 'live' : 'no such room')
      })
      .catch(() => setStatus('offline'))

    return () => {
      cancelled = true
      connection.stop()
    }
  }, [code])

  const next = async () => {
    setBusy(true)
    try {
      const q = await hub.current?.invoke<QuestionView | null>('NextQuestion', code)
      setReveal(null)
      setAnswersIn(0)
      setQuestion(q ?? null)
      if (!q) setSnapshot((s) => (s ? { ...s, state: 'Finished' } : s))
    } finally {
      setBusy(false)
    }
  }

  const doReveal = async () => {
    setBusy(true)
    try {
      const r = await hub.current?.invoke<RevealView | null>('Reveal', code)
      if (r) {
        setReveal(r)
        setQuestion(null)
      }
    } finally {
      setBusy(false)
    }
  }

  const finished = snapshot?.state === 'Finished'
  const joinUrl = `${location.origin}/play/${code}`

  return (
    <div className="shell">
      <header className="bar">
        <div>
          <div className="muted small">Room code</div>
          <div className="code">{code}</div>
        </div>
        <div className="right">
          <div className="muted small">{snapshot?.quizTitle}</div>
          <div>
            <span className={`dot ${status === 'live' ? 'ok' : 'warn'}`} /> {status}
            {' · '}
            {snapshot?.playerCount ?? 0} player{snapshot?.playerCount === 1 ? '' : 's'}
          </div>
        </div>
      </header>

      {!question && !reveal && !finished && (
        <section className="card center">
          <h1>Waiting for players</h1>
          <p className="muted">
            Join at <strong>{joinUrl}</strong>
          </p>
          <p className="big">{snapshot?.playerCount ?? 0} joined</p>
          <button className="primary" onClick={next} disabled={busy || !snapshot?.playerCount}>
            Start the quiz
          </button>
        </section>
      )}

      {question && (
        <section className="card">
          <div className="muted small">
            Question {question.index + 1} of {question.totalQuestions}
          </div>
          <h1>{question.text}</h1>
          <Countdown msRemaining={question.msRemaining} />
          <div className="options">
            {question.options.map((option, i) => (
              <div key={i} className="option static">
                <span className="pill">{'ABCD'[i]}</span>
                {option}
              </div>
            ))}
          </div>
          <p className="muted">
            {answersIn} of {snapshot?.playerCount ?? 0} answered
          </p>
          <button className="primary" onClick={doReveal} disabled={busy}>
            Reveal answer
          </button>
        </section>
      )}

      {reveal && !finished && (
        <section className="card">
          <h2>Answers</h2>
          <div className="options">
            {reveal.countsPerOption.map((count, i) => (
              <div key={i} className={`option ${i === reveal.correctIndex ? 'correct' : 'dim'}`}>
                <span className="pill">{'ABCD'[i]}</span>
                {count} {count === 1 ? 'vote' : 'votes'}
                {i === reveal.correctIndex && <span className="tick">correct</span>}
              </div>
            ))}
          </div>
          <Leaderboard rows={reveal.leaderboard} />
          <button className="primary" onClick={next} disabled={busy}>
            Next question
          </button>
        </section>
      )}

      {finished && (
        <section className="card center">
          <h1>Final scores</h1>
          <Leaderboard rows={snapshot?.leaderboard ?? []} />
        </section>
      )}
    </div>
  )
}

function Leaderboard({ rows }: { rows: { playerId: number; name: string; score: number }[] }) {
  if (!rows.length) return null
  return (
    <ol className="board">
      {rows.map((row, i) => (
        <li key={row.playerId} className={i === 0 ? 'lead' : ''}>
          <span className="rank">{i + 1}</span>
          <span className="name">{row.name}</span>
          <span className="score">{row.score}</span>
        </li>
      ))}
    </ol>
  )
}
