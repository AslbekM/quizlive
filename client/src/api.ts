import * as signalR from '@microsoft/signalr'

// Mirrors QuizLive.Contracts on the server.

export interface QuestionView {
  questionId: number
  index: number
  totalQuestions: number
  text: string
  options: string[]
  timeLimitSeconds: number
  msRemaining: number
}

export interface ScoreRow {
  playerId: number
  name: string
  score: number
}

export interface RevealView {
  questionId: number
  correctIndex: number
  countsPerOption: number[]
  leaderboard: ScoreRow[]
}

export interface PlayerSelf {
  playerId: number
  name: string
  score: number
  reconnectToken: string
  answeredOptionIndex: number | null
  answeredPoints: number | null
}

export interface StateSnapshot {
  code: string
  state: 'Lobby' | 'Question' | 'Reveal' | 'Finished'
  quizTitle: string
  playerCount: number
  question: QuestionView | null
  reveal: RevealView | null
  leaderboard: ScoreRow[]
  you: PlayerSelf | null
  answersIn: number
}

export interface JoinResult {
  ok: boolean
  error: string | null
  snapshot: StateSnapshot | null
}

export interface AnswerResult {
  accepted: boolean
  reason: string | null
  pointsAwarded: number
  totalScore: number
  wasDuplicate: boolean
}

export function createConnection() {
  return new signalR.HubConnectionBuilder()
    .withUrl('/hub')
    // Backs off and retries on its own; every reconnect gets a brand new
    // connection id, which is why both roles re-join on `onreconnected`
    // rather than assuming group membership survived.
    .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
    .build()
}

export async function createSession(quizId = 0): Promise<string> {
  const res = await fetch('/api/sessions', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ quizId }),
  })
  if (!res.ok) throw new Error('Could not start a game.')
  const body = await res.json()
  return body.code as string
}

// --- local identity ---------------------------------------------------
// Keyed by room code so rejoining one game never resurrects another.

interface StoredIdentity {
  playerId: number
  token: string
  name: string
}

const key = (code: string) => `quizlive:${code.toUpperCase()}`

export function rememberIdentity(code: string, identity: StoredIdentity) {
  localStorage.setItem(key(code), JSON.stringify(identity))
}

export function recallIdentity(code: string): StoredIdentity | null {
  const raw = localStorage.getItem(key(code))
  if (!raw) return null
  try {
    return JSON.parse(raw) as StoredIdentity
  } catch {
    return null
  }
}

export function forgetIdentity(code: string) {
  localStorage.removeItem(key(code))
}
