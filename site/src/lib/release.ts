// 빌드할 때 GitHub에서 최신 릴리스를 한 번 읽어 다운로드 단추에 버전과 MSI 주소를 넣는다.
// 다운로드 단추는 늘 최신 주소(releases/latest/download/Foldy-Setup.msi)로 보낸다. 사이트를 다시 만들기 전에
// 새 버전이 나와도 새 파일을 받는다 (사용자 2026-11-25). 그 파일이 없는 옛 릴리스면 버전 붙은 MSI 주소로.
// 실패하면(오프라인 빌드 등) Releases 최신 페이지로 보낸다. 새 릴리스가 나오면 배포 작업이 다시 돈다.
export const REPO = 'chadolkr/Foldy';
export const RELEASES_URL = `https://github.com/${REPO}/releases/latest`;
/** release.yml이 릴리스마다 함께 올리는 버전 없는 이름의 MSI */
export const LATEST_MSI_NAME = 'Foldy-Setup.msi';
export const LATEST_MSI_URL = `${RELEASES_URL}/download/${LATEST_MSI_NAME}`;

export interface LatestRelease {
  version: string | null;
  msiUrl: string;
  /** 이 릴리스의 GitHub 페이지 (바뀐 점) */
  pageUrl: string;
  date: string | null;
}

let cached: Promise<LatestRelease> | null = null;

export function getLatestRelease(): Promise<LatestRelease> {
  cached ??= load();
  return cached;
}

async function load(): Promise<LatestRelease> {
  const fallback: LatestRelease = { version: null, msiUrl: RELEASES_URL, pageUrl: RELEASES_URL, date: null };
  try {
    const headers: Record<string, string> = { Accept: 'application/vnd.github+json' };
    if (process.env.GITHUB_TOKEN) headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`;
    const res = await fetch(`https://api.github.com/repos/${REPO}/releases/latest`, {
      headers,
      signal: AbortSignal.timeout(10_000),
    });
    if (!res.ok) return fallback;
    const data = (await res.json()) as {
      tag_name?: string;
      html_url?: string;
      published_at?: string;
      assets?: { name: string; browser_download_url: string }[];
    };
    const latest = data.assets?.some((a) => a.name === LATEST_MSI_NAME);
    const msi = data.assets?.find((a) => /-x64\.msi$/i.test(a.name));
    return {
      version: data.tag_name?.replace(/^v/, '') ?? null,
      msiUrl: latest ? LATEST_MSI_URL : (msi?.browser_download_url ?? RELEASES_URL),
      pageUrl: data.html_url ?? RELEASES_URL,
      date: data.published_at?.slice(0, 10) ?? null,
    };
  } catch {
    return fallback;
  }
}
