import { Link } from 'react-router'
import { Button, Shell } from '../components/ui'
import { ESCAPE_PALETTE, usePalette } from '../lib/theme'
import { Item, Section } from './HowToPlay'

/**
 * The printable how-to-play sheet for escape rooms: what a game is like, what's on each screen,
 * and the rules that surprise new players (hints cost time, a wrong code locks for a moment).
 * Generic on purpose: it works for every room.
 */
export default function EscapeHowToPlay() {
  usePalette(ESCAPE_PALETTE)
  return (
    <Shell>
      <div className="flex items-start justify-between gap-4">
        <div>
          <p className="text-xs tracking-widest text-accent uppercase">Before you're locked in</p>
          <h1 className="font-display mt-2 text-4xl">How to play an escape room</h1>
        </div>
        <Button className="no-print" onClick={() => window.print()}>
          Print this page
        </Button>
      </div>

      <p className="mt-4 text-lg leading-relaxed">
        You're locked in together, and the clock is running. The room is a series of locked areas, each full of puzzles. Solve every puzzle in
        an area to open the next one, and get out of the last one before time runs out. The catch: the clues are split between your phones, so
        nobody can do it alone.
      </p>

      <Section title="Who's who">
        <Item label="The host">Sets up the game and puts the room on a big screen (a TV or laptop), or shares it on a video call.</Item>
        <Item label="The players">Everyone joins on their own phone with the party code. No app to install, no account.</Item>
        <Item label="The game master">
          The voice of the room. With the AI switched on, it reacts out loud on the big screen and can write a hint for exactly where you're
          stuck, never the answer.
        </Item>
      </Section>

      <Section title="A game at a glance">
        <table className="w-full text-left text-sm">
          <tbody className="divide-y divide-line">
            {[
              ['Join', '5 min', 'Everyone joins on their phone. The host chooses how long (30, 45 or 60 minutes) and how hard (Easy, Normal or Hard).'],
              ['The intro', '1 min', 'The game master explains your predicament. The clock starts and the clues are dealt round.'],
              ['Each area', 'Until it opens', 'Search the scene, share what your phone shows, and solve every puzzle. Then the next area opens.'],
              ['The escape', 'Before time runs out', 'Solve the last area to get out. Your time, plus any hint time, goes on the leaderboard.'],
            ].map(([when, time, what]) => (
              <tr key={when}>
                <td className="py-2 pr-3 align-top font-semibold whitespace-nowrap">{when}</td>
                <td className="py-2 pr-3 align-top whitespace-nowrap text-muted">{time}</td>
                <td className="py-2 align-top">{what}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </Section>

      <Section title="On the big screen">
        <Item label="The room">The area you're in, its puzzles, and the spots you can search.</Item>
        <Item label="The clock">Time left. Every hint takes some away.</Item>
        <Item label="What you've found">The group's items and the latest clues in the notebook.</Item>
        <Item label="What's happened">Every search, solve and hint, as it happens.</Item>
      </Section>

      <Section title="On your phone">
        <Item label="Only you can see these">Your clue pieces. Each phone gets different ones: read them out, compare, and work out what they mean together.</Item>
        <Item label="In front of you">The puzzles in this area, where you type a code or a word and ask for a hint.</Item>
        <Item label="Search">Tap a spot in the scene to search it. What you find goes to the whole group.</Item>
        <Item label="Items">Things you've found. Look closer at an item, or put two together to make something new.</Item>
        <Item label="Notebook">Every clue the group has found by searching, looking closer and putting things together, shared by all phones.</Item>
      </Section>

      <Section title="Puzzles you'll meet">
        <ul className="list-disc space-y-2 pl-5">
          <li>
            <b>Codes and passwords:</b> digits for a lock, or a word or phrase. Capitals, spaces and punctuation don't matter.
          </li>
          <li>
            <b>Locks that need something:</b> a key, a fuse, a tool. Find it first, then use it.
          </li>
          <li>
            <b>Ciphers:</b> a coded message and a decoder. You need the key, which is hidden somewhere in the room. On Normal and Hard there are
            fake keys too, so check which one makes sense.
          </li>
          <li>
            <b>Logic puzzles, number patterns and light panels:</b> work them out together. Each has exactly one answer.
          </li>
        </ul>
      </Section>

      <Section title="Rules that surprise new players">
        <ul className="list-disc space-y-2 pl-5">
          <li>
            <b>Hints cost time.</b> Each one takes minutes off the clock (half as many on Easy). On Hard, hints only nudge.
          </li>
          <li>
            <b>A wrong answer locks that puzzle for 3 seconds</b>, so guessing every code doesn't work.
          </li>
          <li>
            <b>On Hard, searching an empty hiding place costs time.</b> Think before you tap.
          </li>
          <li>
            <b>If someone leaves,</b> their clue pieces pass to someone still playing. Nothing is lost.
          </li>
          <li>
            <b>Every game shuffles the puzzles,</b> so you can play a room again. The puzzle set number at the end lets another group play exactly
            the same ones, and today's challenge gives every group the same puzzles for the day.
          </li>
        </ul>
      </Section>

      <Section title="Host checklist">
        <ul className="list-disc space-y-1 pl-5">
          <li>Before: pick a room and send the party code. Check the room's player count and the age shelf (Family or Adults).</li>
          <li>Put the big screen where everyone can see it, and switch its sound on for the atmosphere.</li>
          <li>Start the clock when everyone has joined: the clues are dealt to whoever is there.</li>
          <li>Let the group struggle a little before suggesting a hint. That's the fun.</li>
        </ul>
      </Section>

      <p className="font-display mt-10 text-center text-xl text-muted italic">Good luck. You'll need it.</p>
      <p className="no-print mt-6 text-center text-sm">
        <Link to="/escape" className="text-accent underline">
          Choose a room
        </Link>
      </p>
    </Shell>
  )
}
