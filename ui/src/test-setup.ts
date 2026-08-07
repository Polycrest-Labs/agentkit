// Vitest setup: JIT-compile component templates and boot the Angular testing platform once.
// Specs run zoneless (no zone.js) — each TestBed config adds provideZonelessChangeDetection().
import '@angular/compiler';
import { ReadableStream, TransformStream, WritableStream } from 'node:stream/web';
import { beforeEach } from 'vitest';
import { TestBed, getTestBed } from '@angular/core/testing';
import { BrowserTestingModule, platformBrowserTesting } from '@angular/platform-browser/testing';

// jsdom ships no streams; the SSE reader tests build Response bodies from them.
globalThis.ReadableStream ??= ReadableStream as unknown as typeof globalThis.ReadableStream;
globalThis.WritableStream ??= WritableStream as unknown as typeof globalThis.WritableStream;
globalThis.TransformStream ??= TransformStream as unknown as typeof globalThis.TransformStream;

getTestBed().initTestEnvironment(BrowserTestingModule, platformBrowserTesting());

beforeEach(() => TestBed.resetTestingModule());
