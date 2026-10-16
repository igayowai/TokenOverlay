<h1 align="center">TokenOverlay</h1>

<p align="center">
Codex · Claude 앱 위에 <b>토큰 수 · 걸린 시간 · 속도</b>와 <b>이번 주 사용량</b>을 띄워 주는 Windows 프로그램
</p>

<p align="center">
  <a href="https://github.com/igayowai/TokenOverlay/releases/latest/download/TokenOverlay-win-Setup.exe">
    <img src="download-button.svg" alt="다운로드 (Windows용 설치 파일)" width="420">
  </a>
</p>

<p align="center">
  ↑ 초록 버튼을 누르면 바로 받아짐 · Windows 10/11 64비트
</p>

---

## 설치 (1분)

1. 위의 **초록 다운로드 버튼**을 누름 → `TokenOverlay-win-Setup.exe` 파일이 받아짐.
2. 받은 파일을 실행함.
   - 브라우저가 "흔히 다운로드되지 않는 파일"이라고 막으면: 다운로드 목록에서 **⋯ → 유지**(Edge) 또는 **계속**(Chrome)을 누름.
   - 파란 창 "Windows의 PC 보호"가 뜨면: **추가 정보** 글자를 누른 뒤 **실행** 버튼을 누름.
   - 개인이 만든 프로그램이라 유료 서명이 없어서 처음 한 번 뜨는 경고임.
3. 끝. 화면 오른쪽 아래 트레이(시계 옆 `^`)에 아이콘이 생김. Codex나 Claude 앱을 켜면 창 위에 캡슐이 붙음.

따로 설치할 것 없음. 새 버전은 자동으로 받아짐.

## 쓰는 법

- Codex나 Claude 데스크톱 앱을 켜면 창 위에 캡슐이 붙고, 앱을 끄면 사라짐.
- 캡슐에 마우스를 올리면 최근 턴 목록과 이번 주 사용량이 펼쳐짐.
- 트레이 아이콘을 누르면 메뉴가 나옴. 자동 실행, 앱 연결, 주간 한도 1분 갱신, 내 기기 공유 같은 설정이 있음.

### 이번 주 사용량 "나 / 다른 곳"

계정의 이번 주 사용량 중에서 내 기기에서 쓴 몫(나)과 나머지(다른 곳)를 나눠 보여 줌. 나머지는 웹, 폰, 다른 사람 등임.
PC와 노트북처럼 여러 대를 쓰면 트레이 메뉴에서 **내 기기 공유 (구글 드라이브)** 를 켜 둠.
- 구글 드라이브 데스크톱 앱이 깔려 있으면 폴더를 알아서 찾음.
- **내 기기 관리…** 에서 기기 이름을 바꾸거나, 기기를 합치거나 뺄 수 있음.

## 업데이트

자동으로 됨. 새 버전이 나오면 알아서 받아 두고, 트레이 메뉴 맨 위에 **업데이트 적용 · 재시작** 이 생김.
- 누르면 몇 초 안에 새 버전으로 다시 켜짐.
- 안 눌러도 다음에 켤 때 적용됨.

## 안 될 때

1. 트레이 메뉴 → 설정 → **버그 신고용 로그 만들기** 를 누름.
2. 바탕화면에 `TokenOverlay-log-….zip` 파일이 생김. 그 파일을 만든 사람에게 보내 주면 됨.

로그에 들어가지 않는 것:
- 대화 내용, 계정 정보
- 채팅 제목 (글자 수만 기록)
- 사용자 폴더 이름 (가려서 기록)

## 개인정보

- 기록은 내 PC(`%LOCALAPPDATA%\CodexTurnOverlay`)에만 저장됨. 내 기기 공유를 켜면 내 구글 드라이브 폴더에도 저장됨. 외부 서버로는 아무것도 보내지 않음.
- 앱 연결을 켜면 Codex·Claude 앱의 사용 기록을 이 PC 안에서만 받아 속도를 계산함. 질문 내용은 받지 않도록 설정됨.
- 기기 구별에는 Windows 기기 고유값을 변환한 짧은 ID만 씀. Codex·Claude 계정 정보는 쓰지 않음.

## 지우기

Windows 설정 → 앱 → 설치된 앱 → **TokenOverlay** 옆 `⋯` → **제거**.
- 자동 실행과 앱 연결도 함께 되돌림.
- 모아 둔 기록 폴더 `%LOCALAPPDATA%\CodexTurnOverlay`는 남음. 필요 없으면 직접 지우면 됨.

## 만든 사람

[soleillevant0125/codex-token-overlay](https://github.com/soleillevant0125/codex-token-overlay)(MIT)를 바탕으로 igayowai가 고쳐 만듦. [LICENSE](LICENSE) 참고.
