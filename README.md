# PDFWriter

Windows 2-in-1 노트북(LG Gram 360 등)에서 **디지털 펜으로 PDF에 직접 필기**하는 가벼운 PDF 필기 프로그램입니다.

강의자료를 열어 필기하고 저장하면, 별도의 필기 파일이나 복사본 없이 **PDF 파일 자체에 필기가 저장**됩니다.

## 특징

- **PDF 자체에 저장**: 필기를 PDF 표준 Ink 주석으로 원본 파일 안에 저장합니다. 원본 PDF를 프로그램 내부로 복제하지 않으므로 저장공간을 추가로 쓰지 않습니다.
- **안전한 저장**: 임시 파일에 먼저 쓰고 → 다시 열어 검증한 뒤 → 원본과 교체합니다. 저장 중 문제가 생겨도 원본은 손상되지 않습니다.
- **펜 중심 설계**: Windows Ink 기반 필기, 필압 표시, 펜 뒤쪽 지우개 지원. 터치는 필기하지 않고 스크롤에 사용합니다.
- **기존 필기 편집**: 이미 PDF에 들어 있는 Ink 주석도 열 때 편집 가능한 필기로 불러옵니다.
- **무료 / 오픈소스 구성**: 렌더링과 주석 처리에 Apache-2.0 라이선스의 PDFium을 사용합니다.

## 기능

| 구분 | 내용 |
|---|---|
| PDF 보기 | 열기, 닫기, 드래그 앤 드롭, 페이지 이동, 페이지 번호 입력, 확대/축소, 페이지 맞춤, 너비 맞춤, 전체화면 |
| 필기 도구 | 펜, 형광펜, 지우개(획 단위), 색상 6종, 굵기 조절 |
| 편집 | Undo / Redo, 현재 페이지 필기 삭제 |
| 저장 | 저장, 다른 이름으로 저장, 종료 시 저장 확인 |

### 단축키

| 키 | 동작 |
|---|---|
| `Ctrl+O` / `Ctrl+S` / `Ctrl+Shift+S` | 열기 / 저장 / 다른 이름으로 저장 |
| `Ctrl+Z` / `Ctrl+Y` | 실행 취소 / 다시 실행 |
| `Ctrl` + `+` / `-` / 마우스 휠 | 확대 / 축소 |
| `←` `→` `PageUp` `PageDown` `Home` `End` | 페이지 이동 |
| `F11` | 전체화면 |

## 개발 예정

- 자동 저장 및 비정상 종료 복구
- PPT / PPTX → PDF 변환 (PowerPoint 또는 LibreOffice 이용)
- 사용자 지정 색상, 썸네일, 검색, 다크 모드, 최근 문서

## 기술 스택

- C# / .NET 9 / WPF (InkCanvas 기반 필기 레이어)
- [PDFium](https://pdfium.googlesource.com/pdfium/) (`PDFiumCore`, Apache-2.0): 렌더링, Ink 주석 읽기/쓰기

## 구조

```text
PdfPen/
├── PdfPen/                      # WPF 애플리케이션
│   ├── Services/
│   │   ├── PdfService/          # PDFium 래퍼, 페이지 좌표 변환
│   │   ├── AnnotationService/   # 필기 <-> PDF Ink 주석 변환
│   │   ├── FileService/         # 임시 파일 + 검증 + 원본 교체 저장
│   │   └── PenService/          # Undo/Redo 기록
│   └── MainWindow.xaml(.cs)
└── Tests/PdfPen.Tests/          # 저장/복원, 좌표, 안전 저장 테스트
```

## 빌드 및 실행

필요 환경: Windows 10/11, [.NET 9 SDK](https://dotnet.microsoft.com/download)

```powershell
git clone https://github.com/ddoggyy/PDFWriter.git
cd PDFWriter
dotnet run --project PdfPen
```

PDF 파일을 인자로 넘겨 바로 열 수도 있습니다.

```powershell
dotnet run --project PdfPen -- "C:\path\to\강의자료.pdf"
```

테스트 실행:

```powershell
dotnet test
```

## 알려진 제한

- 필압에 따른 굵기 변화는 화면에서만 보이고, PDF에는 획마다 하나의 굵기로 저장됩니다 (PDF Ink 주석의 표준 제약).
- 암호로 보호된 PDF는 아직 열 수 없습니다.
- Acrobat 등 다른 뷰어에서의 표시 호환성은 아직 충분히 검증되지 않았습니다.

## 개발 방식

기획, 요구사항 정의, 실기기 테스트는 직접 진행했고,
구현은 [Claude Code](https://claude.com/claude-code)의 도움을 받아 개발했습니다.
