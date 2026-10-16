<p align="center">
  <img src="assets/banner.svg" alt="TokenOverlay — AI가 방금 쓴 토큰·시간·속도를 앱 화면 위에서 바로" width="100%">
</p>

<p align="center">
  <a href="https://github.com/igayowai/TokenOverlay/releases/latest/download/TokenOverlay-win-Setup.exe">
    <img src="assets/download-button.svg" alt="다운로드 (Windows용 설치 파일)" width="380">
  </a>
  <br>
  <sub>Windows 10 · 11 (64비트) &nbsp;·&nbsp; 무료 &nbsp;·&nbsp; 설치 1분</sub>
</p>

<br>

<p align="center">
  <img src="assets/preview.svg" alt="Codex·Claude 앱 위에 뜬 캡슐과 자세히 보기 창" width="100%">
</p>

<p align="center">
  <img src="assets/features.svg" alt="한 턴 통계 · 주간 한도 · 내 기기 합치기 · 자동 업데이트" width="100%">
</p>

## 설치

1. 위의 **초록 다운로드 버튼**을 누름
2. 받은 `TokenOverlay-win-Setup.exe`를 실행함
3. 끝. Codex나 Claude 앱을 켜면 창 위에 캡슐이 붙음

> [!TIP]
> **실행할 때 경고가 뜨면?** 개인이 만든 프로그램이라 처음 한 번 뜨는 정상 경고임.
> - 파란 창 **"Windows의 PC 보호"** → **추가 정보** 글자 누르기 → **실행**
> - 브라우저가 파일을 막으면 → 다운로드 목록에서 **⋯ → 유지**(Edge) 또는 **계속**(Chrome)

## 쓰는 법

- 트레이(화면 오른쪽 아래 시계 옆 `^`)에 있는 아이콘을 누르면 설정 메뉴가 나옴.
- 캡슐에 마우스를 올리면 최근 턴과 이번 주 사용량이 펼쳐짐.
- PC와 노트북을 같이 쓰면 메뉴의 **내 기기 공유 (구글 드라이브)** 를 켜 둠. 두 기기 사용량을 합쳐서 "나"로 셈.
- 업데이트는 자동임. 새 버전이 준비되면 메뉴 맨 위에 **업데이트 적용 · 재시작** 이 생김.

<details>
<summary><b>안 될 때 (버그 신고)</b></summary>
<br>

1. 트레이 메뉴 → 설정 → **버그 신고용 로그 만들기** 를 누름.
2. 바탕화면에 생긴 `TokenOverlay-log-….zip`을 만든 사람에게 보냄.

로그에 대화 내용과 계정 정보는 들어가지 않음. 채팅 제목은 글자 수만 남고, 사용자 폴더 이름은 가려짐.
</details>

<details>
<summary><b>개인정보</b></summary>
<br>

- 기록은 내 PC(`%LOCALAPPDATA%\CodexTurnOverlay`)에만 저장됨. 내 기기 공유를 켜면 내 구글 드라이브에도 저장됨. 외부 서버로 보내는 건 없음.
- 앱 연결을 켜면 Codex·Claude 앱의 사용 기록을 이 PC 안에서만 받아 속도를 계산함. 질문 내용은 받지 않음.
- 기기 구별에는 Windows 기기 고유값을 변환한 짧은 ID를 씀. 계정 정보는 쓰지 않음.
</details>

<details>
<summary><b>지우기</b></summary>
<br>

Windows 설정 → 앱 → 설치된 앱 → **TokenOverlay** 옆 `⋯` → **제거**.
자동 실행과 앱 연결도 함께 원래대로 되돌림. 기록 폴더 `%LOCALAPPDATA%\CodexTurnOverlay`는 남으니, 필요 없으면 직접 지우면 됨.
</details>

<br>

<p align="center"><sub>
<a href="https://github.com/soleillevant0125/codex-token-overlay">soleillevant0125/codex-token-overlay</a>(MIT)를 바탕으로 igayowai가 만듦 · <a href="LICENSE">LICENSE</a>
</sub></p>
