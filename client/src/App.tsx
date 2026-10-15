import { useEffect, useState } from 'react'
import { createSession } from './api'
import { Host } from './views/Host'
import { Play } from './views/Play'
import './styles.css'

/**
 * A three-route app does not need a router dependency. The server has an SPA
 * fallback, so /play/ABC123 is a shareable deep link that survives a refresh.
 */
function useRoute() {
  const [path, setPath] = useState(window.location.pathname)

  useEffect(() => {
    const onPop = () => setPath(window.location.pathname)
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
  }, [])

  const go = (to: string) => {
    window.history.pushState({}, '', to)
    setPath(to)
  }

  return { path, go }
}

export default function App() {
  const { path, go } = useRoute()

  const host = path.match(/^\/host\/([A-Za-z0-9]+)/)
  if (host) return <Host code={host[1].toUpperCase()} />

  const play = path.match(/^\/play\/([A-Za-z0-9]+)/)
  if (play) return <Play code={play[1].toUpperCase()} />

  return <Home go={go} />
}

function Home({ go }: { go: (to: string) => void }) {
  const [code, setCode] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const host = async () => {
    setBusy(true)
    setError(null)
    try {
      go(`/host/${await createSession()}`)
    } catch {
      setError('Could not start a game. Is the server running?')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="shell narrow">
      <section className="card center">
        <h1 className="brand">QuizLive</h1>
        <p className="muted">Run a live quiz. Players join from their phones.</p>

        <button className="primary" onClick={host} disabled={busy}>
          Host a game
        </button>

        <div className="divider">or join one</div>

        <input
          className="field code-input"
          placeholder="ROOM CODE"
          value={code}
          maxLength={8}
          onChange={(e) => setCode(e.target.value.toUpperCase())}
          onKeyDown={(e) => e.key === 'Enter' && code.trim() && go(`/play/${code.trim()}`)}
        />
        <button className="ghost" disabled={!code.trim()} onClick={() => go(`/play/${code.trim()}`)}>
          Join
        </button>

        {error && <p className="error">{error}</p>}
      </section>
    </div>
  )
}
