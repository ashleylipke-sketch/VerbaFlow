import { describe, expect, it } from 'vitest';
import { undoLabel } from './undo';

describe('undoLabel', () => {
  it('writes times as minutes and seconds', () => {
    expect(undoLabel('Edited passage at 65400 ms')).toBe('Edited passage at 1:05');
    expect(undoLabel('Joined the box at 3000 ms to the box above')).toBe('Joined the box at 0:03 to the box above');
  });
  it('leaves other notes as they are', () => {
    expect(undoLabel('Undid: Renamed speaker to Brad')).toBe('Undid: Renamed speaker to Brad');
  });
});
