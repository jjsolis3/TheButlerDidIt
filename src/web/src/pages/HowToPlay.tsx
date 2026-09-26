import type { ReactNode } from 'react'
import { Button, Shell } from '../components/ui'
import { FAQ } from '../lib/guide'

/**
 * The printable how-to-play sheet: a general overview for hosts and guests to read
 * before the party. It is deliberately generic (it works for every mystery); the
 * in-game Guide button gives the step-by-step, moment-by-moment version.
 */
export default function HowToPlay() {
  return (
    <Shell>
      <div className="flex items-start justify-between gap-4">
        <div>
          <p className="text-xs tracking-widest text-accent uppercase">Before the party</p>
          <h1 className="font-display mt-2 text-4xl">How to play</h1>
        </div>
        <Button className="no-print" onClick={() => window.print()}>
          Print this page
        </Button>
      </div>

      <p className="mt-4 text-lg leading-relaxed">
        Someone has been murdered, and everyone at the party is a suspect, including the killer. Each guest plays a character with a past, an
        alibi and secrets. Over the evening you question each other, gather clues, and finally accuse someone. Only the killer knows the truth.
      </p>

      <Section title="Who's who">
        <Item label="The host">Runs the evening from the big screen (a TV or laptop): starts each scene, keeps time, and reveals the truth at the end.</Item>
        <Item label="The guests">Each plays one character. Your phone is your private dossier: never show it to anyone.</Item>
        <Item label="The narrator">Reads the story aloud from the big screen and plays any character nobody took.</Item>
        <Item label="The killer">One of the guests. Their phone tells them. They lie, deflect, and try to get someone else accused.</Item>
      </Section>

      <Section title="The evening at a glance">
        <table className="w-full text-left text-sm">
          <tbody className="divide-y divide-line">
            {[
              ['Arrival', '15–30 min', 'Guests join on their phones, choose a character and settle in.'],
              ['Meet the suspects', '10 min', 'Everyone reads their dossier, then introduces their character to the room.'],
              ['The prologue', '5 min', 'The narrator sets the scene. The murder happens.'],
              ['Each act (usually 3)', '25–30 min each', 'A short scene on the big screen, then mingling: question each other while the timer runs. New clues arrive at the start and halfway.'],
              ['Accusations', '5 min', 'Everyone secretly names the killer, the motive and the method on their phone.'],
              ['The reveal', '10 min', 'Everyone’s guesses, then the unmasking and the true story.'],
              ['Awards', '5 min', 'Vote for the best performance and the best costume. Scores are announced.'],
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

      <Section title="Your phone (your dossier)">
        <Item label="Dossier">Who you are, your alibi, your goals for the evening, what you know, and any lines to say this act.</Item>
        <Item label="Secrets">Things your character wants hidden. Some appear only in later acts. You can reveal one to everyone at any time.</Item>
        <Item label="Clues">Evidence as it’s found. Some clues are given only to you; you can keep them or share them.</Item>
        <Item label="Notes">Your private notebook: who was where, who’s lying.</Item>
        <Item label="Accuse">Opens at the end: choose who did it, why and how.</Item>
      </Section>

      <Section title="Playing your part">
        <ul className="list-disc space-y-2 pl-5">
          <li>Stay in character, talk to everyone, and follow your goals.</li>
          <li>
            <b>Lines:</b> when your phone shows “Say this aloud during this act”, say it during that act’s mingling, whenever it fits: when you’re questioned,
            when the topic comes up, or when the host puts you in the spotlight.
          </li>
          <li>
            <b>Spotlight:</b> the host can give each guest a turn. The big screen shows who’s up, and that guest’s phone says “You’re up!”.
          </li>
          <li>Everyone may lie. The killer must. But nobody shows their phone.</li>
          <li>Stuck for something to say? The big screen shows conversation prompts during mingling, and the Guide button explains what to do next.</li>
        </ul>
      </Section>

      <Section title="Questions new players ask">
        <div className="space-y-3">
          {FAQ.map((f) => (
            <div key={f.q}>
              <p className="font-semibold">{f.q}</p>
              <p className="mt-1 text-sm leading-relaxed">{f.a}</p>
            </div>
          ))}
        </div>
      </Section>

      <Section title="Host checklist">
        <ul className="list-disc space-y-1 pl-5">
          <li>Before: send the invite code, and optionally print the party kit (invitations, name tags, character booklets).</li>
          <li>Put the big screen where everyone can see and hear it. Turn the sound on.</li>
          <li>Start when everyone has chosen a character; unchosen characters go to the narrator.</li>
          <li>During introductions and mingling, use Spotlight so quieter guests get a turn.</li>
          <li>Watch the timer: pause or add time if the room is buzzing, move on if it goes quiet.</li>
          <li>After: share the recap page with your guests.</li>
        </ul>
      </Section>

      <p className="font-display mt-10 text-center text-xl text-muted italic">Have a killer evening.</p>
    </Shell>
  )
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="mt-8">
      <h2 className="font-display mb-3 text-2xl">{title}</h2>
      {children}
    </section>
  )
}

function Item({ label, children }: { label: string; children: ReactNode }) {
  return (
    <p className="mb-2 leading-relaxed">
      <span className="font-semibold">{label}: </span>
      {children}
    </p>
  )
}
