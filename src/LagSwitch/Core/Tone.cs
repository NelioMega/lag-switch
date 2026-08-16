using System.IO;
using System.Media;

namespace LagSwitch.Core;

/// <summary>
/// Deux bips calcules au demarrage, pas de fichier audio a trainer : une chute pour la coupure,
/// une montee pour le retour. En plein ecran, l'oreille dit l'etat plus vite que l'oeil.
/// </summary>
public static class Tone
{
    private const int SampleRate = 44100;

    private static readonly SoundPlayer CutPlayer = Build(760, 300);
    private static readonly SoundPlayer RestorePlayer = Build(420, 840);

    private static DateTime _lastPlayed = DateTime.MinValue;
    private static readonly TimeSpan MinimumGap = TimeSpan.FromMilliseconds(220);

    /// <summary>Joue le bip voulu, en refusant de mitrailler si les bascules s'enchainent.</summary>
    public static void Play(bool cut)
    {
        var now = DateTime.UtcNow;
        if (now - _lastPlayed < MinimumGap) return;
        _lastPlayed = now;

        try { (cut ? CutPlayer : RestorePlayer).Play(); }
        catch { /* pas de peripherique audio : tant pis */ }
    }

    /// <summary>Fabrique un WAV 16 bits mono qui glisse d'une hauteur a l'autre.</summary>
    private static SoundPlayer Build(double startHz, double endHz, int milliseconds = 110)
    {
        var count = SampleRate * milliseconds / 1000;
        var samples = new short[count];
        var phase = 0.0;

        for (var i = 0; i < count; i++)
        {
            var t = (double)i / count;
            var hz = startHz + (endHz - startHz) * t;
            phase += 2 * Math.PI * hz / SampleRate;

            // Attaque courte puis extinction : sans enveloppe, chaque bip claque.
            var attack = Math.Min(1.0, t / 0.04);
            var release = Math.Pow(1 - t, 2.2);
            var amplitude = attack * release * 0.32;

            samples[i] = (short)(Math.Sin(phase) * amplitude * short.MaxValue);
        }

        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        var dataBytes = count * 2;

        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);               // taille du bloc fmt
        w.Write((short)1);         // PCM
        w.Write((short)1);         // mono
        w.Write(SampleRate);
        w.Write(SampleRate * 2);   // octets par seconde
        w.Write((short)2);         // alignement
        w.Write((short)16);        // bits par echantillon
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();

        stream.Position = 0;
        var player = new SoundPlayer(stream);
        player.Load();
        return player;
    }
}
