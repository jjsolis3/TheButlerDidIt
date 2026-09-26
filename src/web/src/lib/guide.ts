import type { PlayerView, StageView } from './types'

/**
 * The live "What now?" guide. A pure function of what the screen already knows
 * (phase, act, step, and for a guest their own dossier), so it never needs its own
 * server call and always matches the moment.
 *
 * Wording principle: say what is happening, then what to do, in the order to do it.
 */
export interface Guide {
  /** One line: where we are in the evening. */
  title: string
  /** What to do now, in order. */
  steps: string[]
  /** An optional tip, shown smaller. */
  tip?: string
}

/** The evening at a glance, in order. Used by the live guide's progress line and the printable guide. */
export const EVENING = [
  { id: 'lobby', label: 'Arrive and choose characters' },
  { id: 'castReveal', label: 'Meet the suspects' },
  { id: 'prologue', label: 'The prologue: the murder' },
  { id: 'act', label: 'Acts: a scene, then mingling' },
  { id: 'accusation', label: 'Accusations' },
  { id: 'reveal', label: 'The reveal' },
  { id: 'awards', label: 'Awards' },
] as const

/** For the big screen. `host` adds the host's to-do items. */
export function stageGuide(stage: StageView, host: boolean): Guide {
  const act = `Act ${stage.actNumber} of ${stage.actCount}`
  switch (stage.phase) {
    case 'lobby':
      return {
        title: 'Before the party',
        steps: host
          ? [
              'Guests join by scanning the QR code or typing the party code on their phones.',
              'Each guest picks a character (or you press "Auto-assign" later). They can read who they are and what to wear, but their secrets stay locked until the evening begins.',
              'Optional: print the party kit (name tags, character booklets) and the how-to-play sheet.',
              'When everyone is here, press "Begin the evening".',
            ]
          : ['Scan the QR code or go to the join page and type the party code.', 'Pick a character on your phone. Dress the part if you like!'],
        tip: 'Characters nobody takes are played by the narrator, so you can start with as few as the minimum number of players.',
      }
    case 'castReveal':
      return {
        title: 'Meet the suspects',
        steps: [
          'Everyone opens their phone and reads their dossier: who they are, their alibi, their goals, and their secrets.',
          'Go round the room: each guest introduces their character in one or two sentences, using the public bio. Stay in character!',
          ...(host ? ['Use "Spotlight" to show whose turn it is to introduce themselves.', 'When everyone has introduced themselves, press "Play the prologue".'] : []),
        ],
        tip: 'Never show your phone to anyone. Everything on it is private to your character.',
      }
    case 'prologue':
      return {
        title: 'The prologue',
        steps: [
          'Watch and listen: the narrator sets the scene and the murder happens.',
          ...(host ? ['When it ends, press "Begin Act One".'] : []),
        ],
      }
    case 'act':
      if (stage.actStep === 'cinematic')
        return {
          title: `${act}: ${stage.actTitle ?? ''} (the scene)`,
          steps: [
            'Watch the scene. Characters played by the narrator speak their lines here.',
            'New clues and, sometimes, newly unlocked secrets arrive when the investigation starts.',
            ...(host ? ['When the scene ends, press "Start mingling".'] : []),
          ],
        }
      return {
        title: `${act}: ${stage.actTitle ?? ''} (investigate)`,
        steps: [
          'Mingle in character until the timer runs out. Question each other about alibis, motives and the clues.',
          'Guests: check your phone. If it shows lines under "Say this aloud during this act", find a good moment to say them.',
          'Read new clues as they appear. More are found halfway through.',
          ...(stage.ai.npcQuestions ? ['Question the characters nobody is playing from the Question tab. The whole room hears the answer.'] : []),
          ...(host
            ? [
                'Use "Spotlight" to give quieter guests a turn: the big screen shows who is up, and their phone tells them what to say.',
                `Pause or add time if a conversation is going well. When the timer ends, press "${stage.actNumber < stage.actCount ? `End Act ${stage.actNumber}` : 'Time for accusations'}".`,
              ]
            : []),
        ],
        tip: stage.prompts[0] ? `Stuck for something to say? Try: “${stage.prompts[0]}”` : undefined,
      }
    case 'accusation':
      return {
        title: 'Accusations',
        steps: [
          'Everyone privately chooses who did it, why and how, on their phone, then locks it in.',
          'You can change your mind until the host reveals the truth.',
          ...(host ? ['When everyone has locked in (the big screen counts), press "Reveal the truth".'] : []),
        ],
        tip: 'The murderer accuses someone else: they score a point for everyone they fool.',
      }
    case 'reveal':
      return {
        title: 'The reveal',
        steps: ['Watch the big screen: first everyone’s guesses, then the killer is unmasked and the whole story is told.', ...(host ? ['Press "Unmask the killer", then "Continue" through each step, then "On to the awards".'] : [])],
      }
    case 'awards':
      return {
        title: 'Awards',
        steps: ['Vote on your phone for the best performance and the best costume.', ...(host ? ['When the votes are in, press "Close voting & announce".'] : [])],
      }
    case 'finished':
      return {
        title: 'That’s a wrap',
        steps: host ? ['Share the recap page with your guests: the cast, the solution, everyone’s secrets and the scores.'] : ['Thanks for playing! The host may share a recap page with everything that happened.'],
      }
  }
}

/** For a guest's phone: the same moment, but personal. */
export function playerGuide(view: PlayerView): Guide {
  const stage = view.stage
  const d = view.dossier
  const lines = d?.linesThisAct ?? []
  const spotlightMe = stage.spotlight?.seatId === view.seatId

  switch (stage.phase) {
    case 'lobby':
      return {
        title: 'Before the party',
        steps: d
          ? [`You are ${d.character.name}. Read "What to wear" and dress the part if you can.`, 'Press "I\'m ready" when you are. Your secrets unlock when the evening begins.']
          : ['Choose a character from the list, or let the host assign one.'],
      }
    case 'castReveal':
      return {
        title: 'Meet the suspects',
        steps: [
          'Read your whole dossier now: Who you are, Your alibi, Your goals, and the Secrets tab.',
          spotlightMe
            ? 'You’re in the spotlight: introduce your character to the room using “What everyone knows about you”.'
            : 'When it’s your turn, introduce your character in a sentence or two. Stay in character from now on!',
        ],
        tip: 'Keep your phone to yourself. Everything on it is private.',
      }
    case 'act':
      if (stage.actStep === 'cinematic') return { title: 'Watch the scene', steps: ['Look up at the big screen and listen. The investigation starts right after.'] }
      return {
        title: `Investigate: ${stage.actTitle ?? ''}`,
        steps: [
          lines.length
            ? `You have ${lines.length} line${lines.length === 1 ? '' : 's'} to say aloud this act (on your Dossier tab). Say ${lines.length === 1 ? 'it' : 'them'} when the moment feels right: when someone questions you, or when the spotlight is on you.`
            : 'You have no scripted lines this act: improvise in character.',
          'Talk to everyone. Ask about alibis and motives; work towards your goals.',
          `Check the Clues tab${view.myClues.some((c) => c.isPrivate) ? ': some clues were given only to you. Share one with everyone if it helps you' : ''}.`,
          d?.isMurderer
            ? 'You’re the killer: lie, deflect and steer suspicion onto someone else. Don’t get caught!'
            : 'Keep your secrets unless revealing one helps clear your name. See "Secrets" below.',
        ],
        tip: spotlightMe ? 'You’re in the spotlight right now: the room is listening to you.' : undefined,
      }
    case 'accusation':
      return {
        title: 'Name the killer',
        steps: d?.isMurderer
          ? ['Accuse someone else to cover your tracks. You earn a point for every guest you fool.']
          : ['On the Accuse tab, choose who did it, why and how, then lock it in. You can change it until the reveal.', 'Points: 3 for the killer, 1 for the motive, 1 for the method.'],
      }
    default:
      return stageGuide(stage, false)
  }
}

/** Answers to the questions new groups ask most, shown in the live guide and on the printable sheet. */
export const FAQ: { q: string; a: string }[] = [
  {
    q: 'When do I say the lines on my phone?',
    a: 'During the investigation (mingling) of the act they belong to. There’s no fixed moment: say them when someone questions you, when the topic comes up, or when the host puts you in the spotlight. Lines for the characters nobody is playing are spoken by the narrator during the scenes.',
  },
  {
    q: 'My dossier says to keep my secrets. How do they ever come out?',
    a: 'Three ways. (1) You choose to reveal one with the “Reveal to everyone” button on the Secrets tab, for example to explain a suspicious clue or clear your name. It then appears on the big screen. (2) Other guests uncover them: many clues hint at someone’s secret, so when you’re confronted with evidence, it’s good play to confess in character. (3) Everything comes out at the end: the recap page shows every secret. Some secrets only “surface” in later acts; your phone tells you when.',
  },
  {
    q: 'Is there a penalty for revealing a secret?',
    a: 'No. Secrets aren’t scored. Most of them make you look guilty without making you the killer, so confessing one can be a smart move. Only the killer’s real secret is truly dangerous to reveal.',
  },
  {
    q: 'Can I lie?',
    a: 'Yes, everyone can. The killer must lie. But never show your phone to anyone.',
  },
  {
    q: 'What are private clues and puzzles?',
    a: 'Some clues are given to just one character. You can keep them or press Share to show everyone. Some clues are puzzles: solving one earns a point and reveals more information.',
  },
  {
    q: 'How does scoring work?',
    a: 'At the accusation: 3 points for naming the killer, 1 for the motive, 1 for the method, plus 1 per puzzle solved. The killer scores 1 for every guest who accused someone else. Everyone also votes for best performance and best costume.',
  },
]
