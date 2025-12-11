using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using NAIGallery.Models;

namespace NAIGallery.Services;

/// <summary>
/// 썸네일 디코딩, 캐싱, UI 적용을 담당하는 파이프라인 인터페이스
/// </summary>
internal interface IThumbnailPipeline
{
    /// <summary>
    /// UI 스레드 디스패처를 초기화합니다.
    /// </summary>
    void InitializeDispatcher(DispatcherQueue dispatcherQueue);
    
    /// <summary>
    /// 단일 이미지의 썸네일을 로드합니다.
    /// </summary>
    Task EnsureThumbnailAsync(ImageMetadata meta, int decodeWidth, CancellationToken ct, bool allowDownscale);
    
    /// <summary>
    /// 여러 이미지의 썸네일을 병렬로 프리로드합니다.
    /// </summary>
    Task PreloadAsync(IEnumerable<ImageMetadata> items, int decodeWidth, CancellationToken ct, int maxParallelism);
    
    /// <summary>
    /// 썸네일 적용을 일시 중지/재개합니다.
    /// </summary>
    void SetApplySuspended(bool suspended);
    
    /// <summary>
    /// 대기 중인 썸네일 적용을 즉시 처리합니다.
    /// </summary>
    void FlushApplyQueue();
    
    /// <summary>
    /// 화면에 보이는 항목의 썸네일을 우선 처리합니다.
    /// </summary>
    void DrainVisible(HashSet<ImageMetadata> visible);
    
    /// <summary>
    /// 썸네일 캐시를 비웁니다.
    /// </summary>
    void ClearCache();
    
    /// <summary>
    /// 캐시 용량 (바이트)
    /// </summary>
    int CacheCapacity { get; set; }

    /// <summary>
    /// 썸네일 로드를 스케줄링합니다.
    /// </summary>
    void Schedule(ImageMetadata meta, int width, bool highPriority = false);
    
    /// <summary>
    /// 화면에 보이는 항목들을 우선 처리합니다.
    /// </summary>
    void BoostVisible(IEnumerable<ImageMetadata> metas, int width);
    
    /// <summary>
    /// 뷰포트가 변경되었을 때 호출합니다.
    /// </summary>
    void UpdateViewport(IReadOnlyList<ImageMetadata> orderedVisible, IReadOnlyList<ImageMetadata> bufferItems, int width);
    
    /// <summary>
    /// 펜딩 상태를 초기화합니다.
    /// </summary>
    void ResetPendingState();
    
    /// <summary>
    /// 썸네일이 적용되었을 때 발생하는 이벤트
    /// </summary>
    event Action<ImageMetadata>? ThumbnailApplied;
}
