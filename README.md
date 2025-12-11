# NAIGallery

NovelAI / Stable Diffusion 이미지를 위한 고성능 갤러리 뷰어입니다. PNG 메타데이터에서 프롬프트, 태그, 생성 파라미터를 자동으로 추출하여 검색 및 탐색할 수 있습니다.

![.NET 10](https://img.shields.io/badge/.NET-10.0-purple)
![WinUI 3](https://img.shields.io/badge/WinUI-3-blue)
![Windows App SDK](https://img.shields.io/badge/Windows%20App%20SDK-1.8-green)

## ✨ 주요 기능

### 🖼️ 갤러리 뷰
- **가상화된 그리드 레이아웃**: 수천 장의 이미지도 부드럽게 스크롤
- **마우스 휠 줌**: Ctrl + 휠로 썸네일 크기 조절
- **정렬 옵션**: 파일명/날짜 기준 오름차순/내림차순 정렬
- **Connected Animation**: 자연스러운 페이지 전환 애니메이션

### 🔍 검색
- **토큰 기반 검색**: 프롬프트, 태그, 파라미터를 빠르게 검색
- **자동완성**: 입력 중 태그 자동 제안
- **Trie 기반 인덱스**: 대용량 컬렉션에서도 빠른 검색

### 📋 메타데이터 추출
- **NovelAI v4 지원**: 구조화된 프롬프트 (base_prompt, character_prompts)
- **Stable Diffusion 호환**: 일반적인 PNG 텍스트 청크 파싱
- **생성 파라미터**: sampler, steps, CFG scale 등 표시

### ⚡ 성능 최적화
- **점진적 썸네일 로드**: 저해상도 → 고해상도 순차 로드
- **LRU 캐시**: 메모리 효율적인 썸네일 캐싱
- **백그라운드 인덱싱**: UI 블로킹 없이 폴더 스캔
- **인덱스 영속화**: JSON 파일로 인덱스 저장/복원

## 🏗️ 아키텍처

```
NAIGallery/
├── Controls/
│   └── AspectPresenter.cs       # 종횡비 유지 컨테이너
├── Converters/
│   └── VisibilityConverters.cs  # XAML 바인딩 컨버터
├── Infrastructure/
│   ├── AppDefaults.cs           # 상수 및 기본값
│   ├── AppSettings.cs           # 앱 설정 관리
│   ├── StringPool.cs            # 문자열 인터닝
│   └── Telemetry.cs             # 성능 계측 (OpenTelemetry)
├── Models/
│   └── ImageMetadata.cs         # 이미지 메타데이터 모델
├── Services/
│   ├── ImageIndexService.cs     # 이미지 인덱싱 서비스
│   ├── PngTextChunkReader.cs    # PNG 청크 파서
│   ├── Metadata/
│   │   ├── IMetadataExtractor.cs
│   │   ├── PngMetadataExtractor.cs
│   │   └── CompositeMetadataExtractor.cs
│   ├── Search/
│   │   ├── TokenSearchIndex.cs  # 토큰 기반 검색 인덱스
│   │   └── SearchTextBuilder.cs # 검색 텍스트 빌더
│   ├── Tags/
│   │   └── TagTrie.cs           # 태그 자동완성 Trie
│   └── Thumbnails/
│       ├── ThumbnailPipeline.cs # 썸네일 로드 파이프라인
│       └── ThumbnailCache.cs    # LRU 썸네일 캐시
├── ViewModels/
│   └── GalleryViewModel.cs      # 갤러리 뷰모델
└── Views/
    ├── GalleryPage.xaml(.cs)    # 메인 갤러리 뷰
    ├── ImageDetailPage.xaml(.cs)# 이미지 상세 뷰
    └── SettingsPage.xaml(.cs)   # 설정 페이지
```

## 🔧 기술 스택

| 기술 | 버전 | 용도 |
|------|------|------|
| .NET | 10.0 | 런타임 |
| Windows App SDK | 1.8 | UI 프레임워크 |
| WinUI 3 | - | XAML UI |
| CommunityToolkit.Mvvm | 8.4 | MVVM 패턴 |
| CommunityToolkit.WinUI | 8.2 | UI 컴포넌트 |
| System.Text.Json | 10.0 | JSON 파싱 |
| MetadataExtractor | 2.9 | EXIF/PNG 메타데이터 |

## 📦 설치

### 요구 사항
- Windows 11 권장

### 빌드
```powershell
git clone https://github.com/resc863/NAIGallery.git
cd NAIGallery
dotnet build -c Release
```

### 실행
Visual Studio 2026에서 솔루션을 열고 F5로 실행하거나:
```powershell
dotnet run --project NAIGallery
```

## 🚀 사용법

1. **폴더 열기**: 툴바의 "폴더 열기" 버튼 클릭
2. **이미지 탐색**: 스크롤 또는 Ctrl+휠로 줌
3. **검색**: 상단 검색창에 태그/프롬프트 입력
4. **상세 보기**: 이미지 클릭 또는 더블 클릭
5. **새로고침**: F5 키로 썸네일 재로드

## 🔄 최근 변경사항

### v1.0 (2025-12)

#### 썸네일 파이프라인 완전 재작성
- **단순화된 구조**: 4개 파일 → 2개 파일로 통합
  - `ThumbnailPipeline.cs`: 단일 파일로 디코딩, 캐싱, UI 적용 통합
  - `ThumbnailCache.cs`: 간단한 LRU 캐시 구현
- **안정성 향상**: 
  - 복잡한 참조 카운팅 제거로 메모리 누수 방지
  - `LayoutCycleException` 유발 요인 제거
  - 타이머 기반 워커 관리로 UI 스레드 과부하 방지
- **성능 개선**:
  - `ConcurrentQueue` 기반 요청 큐 (Channel 대신)
  - 동시 UI 적용 4개로 제한
  - 100ms 간격 타이머로 안정적인 처리

#### 메타데이터 추출
- NovelAI v4 구조화 프롬프트 완벽 지원
- `v4_prompt.caption.char_captions` 파싱 개선
- 캐릭터별 네거티브 프롬프트 병합

#### 검색 기능
- `TokenSearchIndex`: 빠른 토큰 기반 검색
- `TagTrie`: 자동완성 지원
- `SearchTextBuilder`: 검색 텍스트 정규화

#### UI/UX
- Connected Animation으로 부드러운 전환
- AspectPresenter로 정확한 종횡비 유지
- 스크롤 중 썸네일 적용 일시 중지

## 📄 라이선스

Apache 2.0 License

## 🤝 기여

이슈 및 Pull Request 환영합니다!

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request