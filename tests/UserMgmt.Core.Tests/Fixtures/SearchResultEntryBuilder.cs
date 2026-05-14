using System.DirectoryServices.Protocols;
using System.Reflection;

namespace UserMgmt.Core.Tests.Fixtures;

/// <summary>
/// Builds <see cref="SearchResultEntry"/> / <see cref="SearchResponse"/>
/// instances for AdService unit tests.
/// </summary>
/// <remarks>
/// All public constructors on the BCL response types are internal, which
/// is why this helper uses reflection. The alternative — booting an in-proc
/// LDAP server — is out of scope for unit tests. The reflection seams are
/// narrow and pinned to a specific BCL version (10.0.x); when they break,
/// the test failure points directly at this file.
/// </remarks>
internal static class SearchResultEntryBuilder
{
    private static readonly ConstructorInfo EntryCtor =
        typeof(SearchResultEntry).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(SearchResultAttributeCollection)],
            modifiers: null)
        ?? throw new InvalidOperationException("SearchResultEntry(string,SearchResultAttributeCollection) constructor not found.");

    private static readonly ConstructorInfo AttrCollectionCtor =
        typeof(SearchResultAttributeCollection).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [],
            modifiers: null)
        ?? throw new InvalidOperationException("SearchResultAttributeCollection() constructor not found.");

    private static readonly MethodInfo AttrCollectionAdd =
        typeof(SearchResultAttributeCollection).GetMethod(
            "Add",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(DirectoryAttribute)],
            modifiers: null)
        ?? throw new InvalidOperationException("SearchResultAttributeCollection.Add not found.");

    private static readonly ConstructorInfo EntryCollectionCtor =
        typeof(SearchResultEntryCollection).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [],
            modifiers: null)
        ?? throw new InvalidOperationException("SearchResultEntryCollection() constructor not found.");

    private static readonly MethodInfo EntryCollectionAdd =
        typeof(SearchResultEntryCollection).GetMethod(
            "Add",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(SearchResultEntry)],
            modifiers: null)
        ?? throw new InvalidOperationException("SearchResultEntryCollection.Add not found.");

    private static readonly ConstructorInfo ResponseCtor =
        typeof(SearchResponse).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(DirectoryControl[]), typeof(ResultCode), typeof(string), typeof(Uri[])],
            modifiers: null)
        ?? throw new InvalidOperationException("SearchResponse(...) constructor not found.");

    private static readonly PropertyInfo EntriesProperty =
        typeof(SearchResponse).GetProperty(
            "Entries",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("SearchResponse.Entries property not found.");

    private static readonly ConstructorInfo PageResponseCtor =
        typeof(PageResultResponseControl).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(int), typeof(byte[]), typeof(bool), typeof(byte[])],
            modifiers: null)
        ?? throw new InvalidOperationException("PageResultResponseControl(int, byte[], bool, byte[]) constructor not found.");

    /// <summary>Build a <see cref="SearchResultEntry"/> with the given DN and attributes.</summary>
    public static SearchResultEntry Build(string dn, IDictionary<string, object?> attributes)
    {
        ArgumentNullException.ThrowIfNull(dn);
        ArgumentNullException.ThrowIfNull(attributes);

        object collection = AttrCollectionCtor.Invoke([]);
        foreach (KeyValuePair<string, object?> kvp in attributes)
        {
            if (kvp.Value is null)
            {
                continue;
            }

            DirectoryAttribute attr = kvp.Value switch
            {
                string s => new DirectoryAttribute(kvp.Key, s),
                IEnumerable<string> many => new DirectoryAttribute(kvp.Key, [.. many]),
                _ => throw new ArgumentException(
                    $"Unsupported attribute value type {kvp.Value.GetType()} for '{kvp.Key}'."),
            };

            AttrCollectionAdd.Invoke(collection, [kvp.Key, attr]);
        }

        return (SearchResultEntry)EntryCtor.Invoke([dn, collection]);
    }

    /// <summary>
    /// Build a <see cref="PageResultResponseControl"/> whose <see cref="DirectoryControl.GetValue"/>
    /// returns a valid BER-encoded SEQUENCE so <c>DirectoryResponse.Controls</c>
    /// can re-parse it without throwing <see cref="BerConversionException"/>.
    /// </summary>
    private static PageResultResponseControl BuildPageResponseControl(byte[] cookie)
    {
        byte[] controlValue = EncodePageResultControlValue(totalCount: 0, cookie: cookie);
        return (PageResultResponseControl)PageResponseCtor.Invoke([0, cookie, false, controlValue]);
    }

    /// <summary>
    /// Encode the page result control's <c>controlValue</c> as a DER
    /// SEQUENCE { size INTEGER, cookie OCTET STRING }.
    /// </summary>
    private static byte[] EncodePageResultControlValue(int totalCount, byte[] cookie)
    {
        System.Formats.Asn1.AsnWriter writer = new(System.Formats.Asn1.AsnEncodingRules.BER);
        writer.PushSequence();
        writer.WriteInteger(totalCount);
        writer.WriteOctetString(cookie);
        writer.PopSequence();
        return writer.Encode();
    }

    /// <summary>Build a <see cref="SearchResponse"/> containing the supplied entries.</summary>
    public static SearchResponse BuildResponse(
        IEnumerable<SearchResultEntry> entries,
        byte[]? pageCookie = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        DirectoryControl[] controls = pageCookie is null
            ? []
            : [BuildPageResponseControl(pageCookie)];

        SearchResponse response = (SearchResponse)ResponseCtor.Invoke([
            null!,
            controls,
            ResultCode.Success,
            null!,
            Array.Empty<Uri>(),
        ]);

        // Populate the Entries collection on the response.
        object entryCollection = EntriesProperty.GetValue(response)
            ?? EntryCollectionCtor.Invoke([]);
        if (EntriesProperty.GetValue(response) is null)
        {
            EntriesProperty.SetValue(response, entryCollection);
        }

        foreach (SearchResultEntry entry in entries)
        {
            EntryCollectionAdd.Invoke(entryCollection, [entry]);
        }

        return response;
    }
}
