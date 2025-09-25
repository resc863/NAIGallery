using System.Collections.Generic;
using NAIGallery.Models;

namespace NAIGallery.Services;

/// <summary>
/// Token(태그/프롬프트 토큰) 역인덱스 관리 인터페이스.
/// </summary>
internal interface ITokenSearchIndex
{
    /// <summary>메타데이터 객체 토큰화 및 인덱스 반영.</summary>
    void Index(ImageMetadata meta);

    /// <summary>인덱스에서 제거.</summary>
    void Remove(ImageMetadata meta);

    /// <summary>여러 토큰(OR 결합) 후보 조회. 존재하지 않으면 빈 집합.</summary>
    IReadOnlyCollection<ImageMetadata> QueryTokens(IEnumerable<string> tokens);

    /// <summary>접두사 기반 토큰 Suggest.</summary>
    IEnumerable<string> Suggest(string prefix, int limit = 20);
}
