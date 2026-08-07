import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { parseFrame } from './sse';

/**
 * The TS half of the protocol conformance bargain: every fixture in `protocol/fixtures/` either
 * parses through `parseFrame` or is rejected, exactly as declared. The C# suite
 * (`tests/AgentKit.Tests/WireFixtureTests.cs`) round-trips the same files — drift on either side
 * of the mirror fails that repo's CI.
 */

interface Fixture {
  file: string;
  event: string;
  payload?: unknown;
  rawData?: string;
  valid: boolean;
  kind: 'core' | 'domain';
  roundTrip?: boolean;
}

const FIXTURES_DIR = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..', 'protocol', 'fixtures');

const fixtures: Fixture[] = readdirSync(FIXTURES_DIR)
  .filter((f) => f.endsWith('.json'))
  .sort()
  .map((file) => ({ file, ...JSON.parse(readFileSync(join(FIXTURES_DIR, file), 'utf8')) as Omit<Fixture, 'file'> }));

const dataOf = (f: Fixture): string => f.rawData ?? JSON.stringify(f.payload);
const blockOf = (f: Fixture): string => `event: ${f.event}\ndata: ${dataOf(f)}`;

describe('wire fixtures (shared with the C# suite)', () => {
  it('found the fixture directory', () => {
    expect(fixtures.length).toBeGreaterThan(0);
  });

  it('covers every core frame type in the union', () => {
    const coreEvents = new Set(fixtures.filter((f) => f.valid && f.kind === 'core').map((f) => f.event));
    for (const expected of ['loaded', 'token', 'tool', 'question', 'questionForm', 'followups',
      'citation', 'usage', 'heartbeat', 'message', 'done', 'error']) {
      expect(coreEvents, `missing a fixture for core frame '${expected}'`).toContain(expected);
    }
  });

  for (const fixture of fixtures) {
    if (fixture.valid) {
      it(`${fixture.file}: parses with type '${fixture.event}'`, () => {
        const frame = parseFrame<{ type: string }>(blockOf(fixture));
        expect(frame).not.toBeNull();
        // The event name is the sole discriminant — even when the payload carries a hostile
        // `type` member (hostile-type.json), the parsed type must equal the event name.
        expect(frame!.type).toBe(fixture.event);
        // Every payload field except a hostile `type` survives the reassembly.
        const payload = fixture.payload as Record<string, unknown>;
        for (const key of Object.keys(payload)) {
          if (key === 'type') continue;
          expect(frame![key as keyof typeof frame], `${fixture.file}: field '${key}' lost in parsing`)
            .toEqual(payload[key]);
        }
      });
    } else {
      it(`${fixture.file}: is rejected (null)`, () => {
        expect(parseFrame(blockOf(fixture))).toBeNull();
      });
    }
  }
});
