using System.Collections.Generic;
using NAIGallery.Models;

namespace NAIGallery.Services;

/// <summary>
/// Token(�±�/������Ʈ ��ū) ���ε��� ���� �������̽�.
/// </summary>
internal interface ITokenSearchIndex
{
    /// <summary>��Ÿ������ ��ü ��ūȭ �� �ε��� �ݿ�.</summary>
    void Index(ImageMetadata meta);

    /// <summary>�ε������� ����.</summary>
    void Remove(ImageMetadata meta);

    /// <summary>���� ��ū(OR ����) �ĺ� ��ȸ. �������� ������ �� ����.</summary>
    IReadOnlyCollection<ImageMetadata> QueryTokens(IEnumerable<string> tokens);

    /// <summary>���λ� ��� ��ū Suggest.</summary>
    IEnumerable<string> Suggest(string prefix, int limit = 20);
}
