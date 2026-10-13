import { describe, expect, it } from 'vitest';
import { Downsampler, from, join, median, retryDelay, toPcm16 } from './live';

describe('Downsampler', () => {
  it('makes a third as many samples from 48 kHz, however the input is cut up', () => {
    const d = new Downsampler(48000);
    let n = 0;
    for (let i = 0; i < 100; i++) n += d.push(new Float32Array(128).fill(0.5)).length;
    expect(Math.abs(n - 12800 / 3)).toBeLessThanOrEqual(2);
  });

  it('keeps a steady level and the same result whatever the piece size', () => {
    const tone = Float32Array.from({ length: 4800 }, (_, i) => Math.sin(i / 40));
    const one = new Downsampler(48000).push(tone);
    const d = new Downsampler(48000);
    const parts: Float32Array[] = [];
    for (let i = 0; i < tone.length; i += 100) parts.push(d.push(tone.slice(i, i + 100)));
    const many = join(parts);
    expect(many.length).toBe(one.length);
    for (let i = 0; i < one.length; i++) expect(many[i]).toBeCloseTo(one[i], 5);
    expect(new Downsampler(48000).push(new Float32Array(300).fill(0.25)).every(v => Math.abs(v - 0.25) < 1e-6)).toBe(true);
  });

  it('passes 16 kHz through and copes with 44.1 kHz', () => {
    expect(new Downsampler(16000).push(new Float32Array(10)).length).toBe(10);
    expect(Math.abs(new Downsampler(44100).push(new Float32Array(44100)).length - 16000)).toBeLessThanOrEqual(2);
  });
});

describe('live helpers', () => {
  it('writes 16-bit little-endian PCM, clipped', () => {
    const v = new DataView(toPcm16(Float32Array.from([0, 1, -1, 2])));
    expect([v.getInt16(0, true), v.getInt16(2, true), v.getInt16(4, true), v.getInt16(6, true)]).toEqual([0, 32767, -32768, 32767]);
  });
  it('joins pieces and reads from a point', () => {
    const chunks = [Float32Array.from([1, 2]), Float32Array.from([3, 4, 5])];
    expect(Array.from(join(chunks))).toEqual([1, 2, 3, 4, 5]);
    expect(Array.from(from(chunks, 3))).toEqual([4, 5]);
    expect(Array.from(from(chunks, 9))).toEqual([]);
  });
  it('takes the middle value and backs off when reconnecting', () => {
    expect(median([])).toBeNull();
    expect(median([5, 1, 3])).toBe(3);
    expect(median([1, 2, 3, 10])).toBe(3);
    expect([0, 1, 2, 3, 4, 9].map(retryDelay)).toEqual([1000, 2000, 4000, 8000, 10000, 10000]);
  });
});
