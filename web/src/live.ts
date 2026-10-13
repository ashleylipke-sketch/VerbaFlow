import { RATE } from './takes';

/**
 * Speech mode's audio: the microphone arrives at the sound card's rate (usually 48 kHz) and goes to the speech service
 * and the saved recording at 16 kHz. Each output sample is the average of the input samples around it, which also
 * filters out sound too high to keep at 16 kHz.
 */
export class Downsampler {
  private buf = new Float32Array(0);
  private next = 0; // where the next output sample sits, in input samples from the start of buf
  private readonly ratio: number;
  constructor(from: number, to = RATE) { this.ratio = from / to; }

  push(input: Float32Array): Float32Array {
    if (this.ratio === 1) return input.slice();
    const buf = new Float32Array(this.buf.length + input.length);
    buf.set(this.buf); buf.set(input, this.buf.length);
    const half = this.ratio / 2;
    const out: number[] = [];
    while (this.next + half < buf.length) {
      const a = Math.max(0, Math.floor(this.next - half)), b = Math.min(buf.length, Math.max(a + 1, Math.floor(this.next + half)));
      let sum = 0; for (let i = a; i < b; i++) sum += buf[i];
      out.push(sum / (b - a));
      this.next += this.ratio;
    }
    // Keep what the next output samples still need.
    const keepFrom = Math.max(0, Math.floor(this.next - half) - 1);
    this.buf = buf.slice(keepFrom);
    this.next -= keepFrom;
    return Float32Array.from(out);
  }
}

/** Samples as 16-bit little-endian PCM, what the live speech service reads. */
export function toPcm16(samples: Float32Array): ArrayBuffer {
  const out = new DataView(new ArrayBuffer(samples.length * 2));
  for (let i = 0; i < samples.length; i++) {
    const x = Math.max(-1, Math.min(1, samples[i]));
    out.setInt16(i * 2, x < 0 ? x * 0x8000 : x * 0x7fff, true);
  }
  return out.buffer;
}

/** All the pieces as one track. */
export function join(chunks: Float32Array[]): Float32Array {
  const out = new Float32Array(chunks.reduce((n, c) => n + c.length, 0));
  let at = 0;
  for (const c of chunks) { out.set(c, at); at += c.length; }
  return out;
}

/** The samples from `from` onwards, across the pieces. */
export function from(chunks: Float32Array[], start: number): Float32Array {
  const all = join(chunks);
  return all.slice(Math.max(0, Math.min(all.length, start)));
}

/** The middle value (rounded), or null with none. */
export function median(values: number[]): number | null {
  if (!values.length) return null;
  const s = [...values].sort((a, b) => a - b), m = Math.floor(s.length / 2);
  return Math.round(s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2);
}

/** How long to wait before trying to connect again: 1, 2, 4, 8, then every 10 seconds. */
export const retryDelay = (attempt: number) => Math.min(10_000, 1000 * 2 ** Math.max(0, attempt));

/** The worklet that hands the microphone's samples to the page in pieces of about 40 ms. */
export const TAP = `class VfTap extends AudioWorkletProcessor {
  constructor() { super(); this.buf = new Float32Array(2048); this.n = 0; }
  process(inputs) {
    const ch = inputs[0] && inputs[0][0];
    if (ch) for (let i = 0; i < ch.length; i++) {
      this.buf[this.n++] = ch[i];
      if (this.n === this.buf.length) { this.port.postMessage(this.buf.slice(0)); this.n = 0; }
    }
    return true;
  }
}
registerProcessor('vf-tap', VfTap);`;
