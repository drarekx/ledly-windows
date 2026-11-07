namespace Ledly;

/// <summary>
/// Effects. Member names are the API/settings raw values: never rename them.
/// The WLED effect number is the position here, so new effects go at the end
/// (and in their place in <see cref="EffectKindInfo.Ordered"/>).
/// </summary>
public enum EffectKind
{
    rainbowSwirl, doubleSwirl, rainbowWaves, rainbowMood, breath, candle, fire, knightRider, police, strobe,
    sparks, moodBlobs, seaWaves, plasma, snake, colorWipe, christmas, lightClock, aurora, meteor,
    musicPulse, musicSpectrum, musicWaves, musicMeter,
    // Added later; appended so earlier WLED effect numbers don't move.
    atomicSwirl, colorWaves, moodBlue, moodGreen, moodRed, moodCold, moodWarm,
    policeSolid, cinemaBrighten, cinemaDim, notify, shutdown,
}
