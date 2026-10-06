namespace ButlerDidIt.Game.Scenarios;

/// <summary>
/// Background sound presets, synthesised live on the TV (src/web/src/lib/sound.ts), so mysteries and escape rooms
/// need no audio files and nothing to license. New presets need a matching one there.
///
/// Both games use them: an escape room names one for the room and each stage, and a mystery takes its theme's
/// (or names its own, or one per act). A host's uploaded recording plays instead.
/// </summary>
public enum Soundscape
{
    /// <summary>No background sound.</summary>
    Silence,

    /// <summary>A low, uneasy hum: fits anywhere.</summary>
    Drone,

    /// <summary>Machinery hum, a ticking clock and the odd drip.</summary>
    Workshop,

    /// <summary>A slightly out-of-tune music box over a crowd murmur.</summary>
    Carnival,

    /// <summary>Waves and wind.</summary>
    Sea,

    /// <summary>A slow, pulsing synth pad and faint beeps.</summary>
    Space,

    /// <summary>Wind and distant, low bells.</summary>
    Haunted,

    /// <summary>A grandfather clock and a crackling fire: a country house, a grand hotel.</summary>
    Manor,

    /// <summary>Rain on the windows and distant thunder.</summary>
    Storm,

    /// <summary>Wheels clacking over the rails and a low rumble.</summary>
    Train,

    /// <summary>Crickets and a soft wind: a warm night outdoors.</summary>
    Night,

    /// <summary>A crowd's murmur and slow, soft chords: a club, a party, a premiere.</summary>
    Lounge,

    /// <summary>After hours in a games hall: a buzzing light, a fan, the odd game blip and a far-off music box.</summary>
    Arcade,

    /// <summary>A crowd waiting for the show, with the beat thumping through the wall and a synth sparkling over it.</summary>
    Concert,

    /// <summary>A big crowd that swells and cheers, and a horn now and then: a stadium, a tournament.</summary>
    Stadium,

    /// <summary>A breeze, birdsong and a few soft piano notes: fields and woods on a calm day.</summary>
    Meadow,

    /// <summary>Drips that echo, a low rumble and the odd tumble of stones: a cave, a mine.</summary>
    Cave,

    /// <summary>A slow, steady pulse under a low hum, and an eerie glide now and then: someone's coming.</summary>
    Tension,
}
