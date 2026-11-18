// GitHub REST 최소 클라이언트 (Issues만). 토큰은 Worker secret GITHUB_TOKEN.

export interface Issue {
  number: number;
  html_url: string;
  state: "open" | "closed";
}

export class GitHubError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

export class GitHub {
  constructor(
    private readonly token: string,
    private readonly repo: string,
    private readonly fetcher: typeof fetch = fetch,
  ) {}

  private async call<T>(method: string, path: string, body?: unknown): Promise<T> {
    const res = await this.fetcher(`https://api.github.com${path}`, {
      method,
      headers: {
        Authorization: `Bearer ${this.token}`,
        Accept: "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "foldy-crash-relay",
        ...(body ? { "Content-Type": "application/json" } : {}),
      },
      body: body ? JSON.stringify(body) : undefined,
    });
    if (!res.ok) {
      const text = await res.text().catch(() => "");
      throw new GitHubError(res.status, `GitHub ${method} ${path} → ${res.status} ${text.slice(0, 300)}`);
    }
    return (await res.json()) as T;
  }

  getIssue(n: number): Promise<Issue> {
    return this.call("GET", `/repos/${this.repo}/issues/${n}`);
  }

  async createIssue(title: string, body: string, labels: string[]): Promise<Issue> {
    try {
      return await this.call("POST", `/repos/${this.repo}/issues`, { title, body, labels });
    } catch (e) {
      // 라벨 권한이 없거나 라벨 검증에 실패하면 라벨 없이 다시 시도
      if (e instanceof GitHubError && e.status === 422 && labels.length > 0)
        return this.call("POST", `/repos/${this.repo}/issues`, { title, body });
      throw e;
    }
  }

  async comment(n: number, body: string): Promise<void> {
    await this.call("POST", `/repos/${this.repo}/issues/${n}/comments`, { body });
  }

  /** KV가 비었을 때(재배포·삭제) 제목의 [crash:서명] 태그로 기존 이슈를 찾는다. */
  async findBySignature(signature: string): Promise<Issue | undefined> {
    const q = encodeURIComponent(`repo:${this.repo} is:issue in:title "crash:${signature}"`);
    const r = await this.call<{ items: Issue[] }>("GET", `/search/issues?q=${q}&sort=created&order=desc&per_page=1`);
    return r.items[0];
  }
}
