import { useEffect, useRef, useState } from 'react'

/**
 * Counts down from the time the server says is left, not from the question's
 * full length. That distinction is what makes a reconnecting client land on
 * the correct remaining time instead of quietly granting itself a fresh timer.
 */
export function Countdown({ msRemaining }: { msRemaining: number }) {
  const deadline = useRef(Date.now() + msRemaining)
  const [left, setLeft] = useState(msRemaining)

  useEffect(() => {
    deadline.current = Date.now() + msRemaining
    setLeft(msRemaining)

    const tick = setInterval(() => {
      setLeft(Math.max(0, deadline.current - Date.now()))
    }, 100)

    return () => clearInterval(tick)
  }, [msRemaining])

  const seconds = Math.ceil(left / 1000)
  const urgent = left <= 5000

  return (
    <div className={`countdown ${urgent ? 'urgent' : ''}`} aria-live="off">
      {seconds}s
    </div>
  )
}
