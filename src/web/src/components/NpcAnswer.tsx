import { useNpcAnswer } from "../lib/hub";
import type { InterrogationView } from "../lib/types";

/** An NPC's answer, "typed" as the AI writes it, with a blinking cursor until it's finished. */
export function NpcAnswer({
  interrogation,
  placeholder,
  className,
}: {
  interrogation: InterrogationView;
  placeholder: string;
  className: string;
}) {
  const { text, typing } = useNpcAnswer(interrogation);
  if (text === null)
    return (
      <p className={`${className} candle text-muted italic`}>{placeholder}</p>
    );
  return (
    <p className={className} data-testid={typing ? "npc-typing" : undefined}>
      {text}
      {typing && (
        <span
          className="ml-0.5 inline-block animate-pulse text-accent"
          aria-hidden
        >
          ▍
        </span>
      )}
    </p>
  );
}
