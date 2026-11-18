import { describe, expect, it } from "vitest";
import worker, { relay, type Env } from "../src/index";
import { GitHub } from "../src/github";
import { crashSignature, normalizeFrame, validateReport, type CrashReport } from "../src/report";
import { neutralizeMarkdown, scrubText } from "../src/scrub";

class MemoryKV {
  data = new Map<string, string>();
  async get(key: string, type?: "json") {
    const v = this.data.get(key);
    if (v === undefined) return null;
    return type === "json" ? JSON.parse(v) : v;
  }
  async put(key: string, value: string) {
    this.data.set(key, value);
  }
}

interface Call {
  method: string;
  path: string;
  body?: any;
}

/** GitHub API 가짜. 이슈 저장소를 메모리에 둔다. */
function fakeGitHub() {
  const issues = new Map<number, { number: number; html_url: string; state: "open" | "closed"; title: string }>();
  const calls: Call[] = [];
  let next = 1;
  const fetcher = (async (input: string, init: RequestInit) => {
    const u = new URL(input);
    const method = init.method ?? "GET";
    const body = init.body ? JSON.parse(init.body as string) : undefined;
    calls.push({ method, path: u.pathname + u.search, body });
    const reply = (status: number, b: unknown) => new Response(JSON.stringify(b), { status });
    let m: RegExpExecArray | null;
    if (method === "POST" && u.pathname === "/repos/o/r/issues") {
      const n = next++;
      const issue = { number: n, html_url: `https://github.com/o/r/issues/${n}`, state: "open" as const, title: body.title };
      issues.set(n, issue);
      return reply(201, issue);
    }
    if ((m = /^\/repos\/o\/r\/issues\/(\d+)$/.exec(u.pathname)) && method === "GET") {
      const issue = issues.get(Number(m[1]));
      return issue ? reply(200, issue) : reply(404, {});
    }
    if ((m = /^\/repos\/o\/r\/issues\/(\d+)\/comments$/.exec(u.pathname)) && method === "POST") return reply(201, {});
    if (u.pathname === "/search/issues") {
      const q = u.searchParams.get("q") ?? "";
      const sig = /crash:([0-9a-f]+)/.exec(q)?.[1];
      const items = [...issues.values()].filter((i) => sig && i.title.includes(`crash:${sig}`)).reverse();
      return reply(200, { items });
    }
    return reply(500, { path: u.pathname });
  }) as unknown as typeof fetch;
  return { gh: new GitHub("t", "o/r", fetcher), issues, calls };
}

const sample = (over: Partial<CrashReport> = {}): CrashReport => ({
  schema: 1,
  appVersion: "0.0.1",
  osVersion: "Windows 10.0.26100",
  runtime: ".NET 10.0.1",
  exceptionType: "System.InvalidOperationException",
  message: "boom",
  details: "System.InvalidOperationException: boom\n   at Foldy.App.MainWindow.Open() in C:\\Users\\kim\\src\\Foldy\\MainWindow.xaml.cs:line 42",
  frames: ["Foldy.App.MainWindow.Open", "System.Windows.Threading.Dispatcher.Invoke"],
  ...over,
});

const env = (kv = new MemoryKV(), extra: Partial<Env> = {}): Env =>
  ({ GITHUB_TOKEN: "t", GITHUB_REPO: "o/r", CRASH_KV: kv as unknown as KVNamespace, ...extra }) as Env;

describe("scrub", () => {
  it("경로는 파일 이름만 남긴다", () => {
    expect(scrubText("at X() in C:\\Users\\kim\\src\\Foo.cs:line 42")).toBe("at X() in <path>\\Foo.cs:line 42");
    expect(scrubText("Could not find file 'D:\\비밀\\계약서 폴더\\a b.docx'.")).toBe("Could not find file '<path>\\a b.docx'.");
    expect(scrubText("열기 실패: D:\\비밀\\계약서.")).toBe("열기 실패: <path>.");
    expect(scrubText("\"C:\\Users\\kim\\My Docs\"")).toBe("\"<path>\"");
    expect(scrubText("\\\\server\\share\\a.txt 없음")).toBe("<path>\\a.txt 없음");
    expect(scrubText("mail kim@example.com")).toBe("mail <email>");
  });
  it("마크다운 탈출과 멘션을 막는다", () => {
    expect(neutralizeMarkdown("```\n@octocat")).toBe("ʼʼʼ\n@\u200boctocat");
  });
});

describe("signature", () => {
  it("줄 번호·람다 번호가 달라도 같은 서명", async () => {
    const a = await crashSignature(sample({ frames: ["at Foldy.A.<Load>b__3_0() in <path>\\A.cs:line 1"] }));
    const b = await crashSignature(sample({ frames: ["at Foldy.A.<Load>b__7_2() in <path>\\A.cs:line 9"] }));
    expect(a).toBe(b);
    expect(a).toMatch(/^[0-9a-f]{12}$/);
  });
  it("예외 타입이 다르면 다른 서명", async () => {
    expect(await crashSignature(sample())).not.toBe(await crashSignature(sample({ exceptionType: "System.IO.IOException" })));
  });
  it("Foldy 프레임을 우선한다", async () => {
    const a = await crashSignature(sample({ frames: ["System.X.Y", "Foldy.A.B"] }));
    const b = await crashSignature(sample({ frames: ["System.Z.W", "Foldy.A.B"] }));
    expect(a).toBe(b);
  });
  it("frame 정규화", () => {
    expect(normalizeFrame("at Foldy.A.<Run>d__4.MoveNext() in x")).toBe("Foldy.A.<Run>async.MoveNext()");
  });
});

describe("validate", () => {
  it("필수 필드와 형식", () => {
    expect(validateReport(sample()).ok).toBe(true);
    expect(validateReport({ ...sample(), schema: 2 }).ok).toBe(false);
    expect(validateReport({ ...sample(), appVersion: "abc" }).ok).toBe(false);
    expect(validateReport({ ...sample(), exceptionType: "bad type!" }).ok).toBe(false);
    expect(validateReport({ ...sample(), frames: "x" }).ok).toBe(false);
  });
});

describe("relay", () => {
  it("처음은 이슈, 다음은 댓글로 합친다", async () => {
    const kv = new MemoryKV();
    const { gh, issues, calls } = fakeGitHub();
    const r1 = await relay(sample(), env(kv), gh);
    const r2 = await relay(sample({ appVersion: "0.0.2" }), env(kv), gh);
    expect(r1.status).toBe("created");
    expect(r2).toMatchObject({ status: "commented", issue: r1.issue, occurrences: 2 });
    expect(issues.size).toBe(1);
    const created = calls.find((c) => c.method === "POST" && c.path === "/repos/o/r/issues")!;
    expect(created.body.title).toMatch(/^\[crash:[0-9a-f]{12}\] InvalidOperationException: boom$/);
    expect(created.body.body).not.toContain("kim");
    expect(created.body.labels).toEqual(["crash", "auto-report"]);
  });

  it("닫힌 이슈와 같은 서명이면 재발 이슈를 새로 만든다", async () => {
    const kv = new MemoryKV();
    const { gh, issues } = fakeGitHub();
    const r1 = await relay(sample(), env(kv), gh);
    issues.get(r1.issue)!.state = "closed";
    const r2 = await relay(sample(), env(kv), gh);
    expect(r2.status).toBe("created");
    expect(r2.issue).not.toBe(r1.issue);
  });

  it("KV가 비어도 제목 검색으로 기존 이슈를 찾는다", async () => {
    const { gh, issues } = fakeGitHub();
    await relay(sample(), env(new MemoryKV()), gh);
    const r = await relay(sample(), env(new MemoryKV()), gh);
    expect(r.status).toBe("commented");
    expect(issues.size).toBe(1);
  });

  it("댓글 상한을 넘으면 횟수만 센다", async () => {
    const kv = new MemoryKV();
    const { gh } = fakeGitHub();
    const e = env(kv, { MAX_COMMENTS_PER_ISSUE: "2" });
    await relay(sample(), e, gh);
    expect((await relay(sample(), e, gh)).status).toBe("commented");
    expect((await relay(sample(), e, gh)).status).toBe("counted");
  });

  it("하루 새 이슈 상한", async () => {
    const kv = new MemoryKV();
    const { gh } = fakeGitHub();
    const e = env(kv, { MAX_NEW_ISSUES_PER_DAY: "1" });
    await relay(sample(), e, gh);
    await expect(relay(sample({ exceptionType: "System.Other" }), e, gh)).rejects.toThrow("상한");
  });
});

describe("fetch handler", () => {
  const post = (body: string, headers: Record<string, string> = {}) =>
    new Request("https://relay.example/v1/crash", { method: "POST", body, headers });

  it("라우팅과 검증 오류", async () => {
    expect((await worker.fetch(new Request("https://relay.example/health"), env())).status).toBe(200);
    expect((await worker.fetch(new Request("https://relay.example/v1/crash"), env())).status).toBe(405);
    expect((await worker.fetch(post("{"), env())).status).toBe(400);
    expect((await worker.fetch(post("x".repeat(70_000)), env())).status).toBe(413);
  });

  it("rate limit", async () => {
    const limiter = { limit: async () => ({ success: false }) } as unknown as RateLimit;
    const res = await worker.fetch(post(JSON.stringify(sample())), env(new MemoryKV(), { RATE_LIMITER: limiter }));
    expect(res.status).toBe(429);
  });
});
