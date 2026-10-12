/**
 * Dictation audio as it is being made: one track of samples at 16 kHz (enough for speech), built from takes.
 * A new take can be added at the end, put in at a point (the rest moves along), or recorded over what is there.
 * Saved as a WAV file, which the speech service and every browser can read.
 */
export const RATE = 16000;

export type TakeMode = 'end' | 'insert' | 'overwrite';

/** The track with a take put in. `at` is a sample position; it is ignored when adding at the end. */
export function splice(track: Float32Array, take: Float32Array, at: number, mode: TakeMode): Float32Array {
  const pos = mode === 'end' ? track.length : Math.max(0, Math.min(track.length, Math.round(at)));
  if (mode === 'overwrite') {
    const out = new Float32Array(Math.max(track.length, pos + take.length));
    out.set(track);
    out.set(take, pos);
    return out;
  }
  const out = new Float32Array(track.length + take.length);
  out.set(track.subarray(0, pos));
  out.set(take, pos);
  out.set(track.subarray(pos), pos + take.length);
  return out;
}

/** The samples as a 16-bit mono WAV file. */
export function toWav(samples: Float32Array): Blob {
  const data = samples.length * 2;
  const buf = new ArrayBuffer(44 + data);
  const v = new DataView(buf);
  const text = (o: number, s: string) => { for (let i = 0; i < s.length; i++) v.setUint8(o + i, s.charCodeAt(i)); };
  text(0, 'RIFF'); v.setUint32(4, 36 + data, true); text(8, 'WAVE');
  text(12, 'fmt '); v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true);
  v.setUint32(24, RATE, true); v.setUint32(28, RATE * 2, true); v.setUint16(32, 2, true); v.setUint16(34, 16, true);
  text(36, 'data'); v.setUint32(40, data, true);
  for (let i = 0; i < samples.length; i++) {
    const x = Math.max(-1, Math.min(1, samples[i]));
    v.setInt16(44 + i * 2, x < 0 ? x * 0x8000 : x * 0x7fff, true);
  }
  return new Blob([buf], { type: 'audio/wav' });
}

/** Any recording the browser can play (a take from the microphone, or a saved WAV), as one track at 16 kHz. */
export async function decode(blob: Blob): Promise<Float32Array> {
  const ctx = new AudioContext();
  try {
    const decoded = await ctx.decodeAudioData(await blob.arrayBuffer());
    const length = Math.max(1, Math.ceil(decoded.duration * RATE));
    const offline = new OfflineAudioContext(1, length, RATE); // one channel: stereo is mixed down
    const src = offline.createBufferSource();
    src.buffer = decoded; src.connect(offline.destination); src.start();
    return (await offline.startRendering()).getChannelData(0).slice();
  } finally { ctx.close(); }
}

export const msOf = (samples: number) => Math.round(samples * 1000 / RATE);
export const samplesAt = (ms: number) => Math.round(ms * RATE / 1000);
