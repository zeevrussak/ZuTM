// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using ZuTM.Core.Plist;
using Xunit;

namespace ZuTM.Core.Tests.Utm;

/// <summary>Order-insensitive structural equality for plist trees (plist dictionaries are unordered maps).</summary>
public static class PlistAssert
{
    public static void Equal(PlistNode? expected, PlistNode? actual, string path = "$")
    {
        switch (expected, actual)
        {
            case (null, null):
                return;
            case (PlistDictionary expectedDict, PlistDictionary actualDict):
                Assert.True(
                    expectedDict.Keys.ToHashSet().SetEquals(actualDict.Keys),
                    $"{path}: key sets differ; expected [{string.Join(",", expectedDict.Keys)}] actual [{string.Join(",", actualDict.Keys)}]");
                foreach (var key in expectedDict.Keys)
                {
                    Equal(expectedDict[key], actualDict[key], $"{path}.{key}");
                }

                return;
            case (PlistArray expectedArray, PlistArray actualArray):
                Assert.Equal(expectedArray.Count, actualArray.Count);
                for (var i = 0; i < expectedArray.Count; i++)
                {
                    Equal(expectedArray[i], actualArray[i], $"{path}[{i}]");
                }

                return;
            case (PlistString expectedString, PlistString actualString):
                Assert.Equal(expectedString.Value, actualString.Value);
                return;
            case (PlistInteger expectedInt, PlistInteger actualInt):
                Assert.Equal(expectedInt.Value, actualInt.Value);
                return;
            case (PlistReal expectedReal, PlistReal actualReal):
                Assert.Equal(expectedReal.Value, actualReal.Value);
                return;
            case (PlistBoolean expectedBool, PlistBoolean actualBool):
                Assert.Equal(expectedBool.Value, actualBool.Value);
                return;
            case (PlistData expectedData, PlistData actualData):
                Assert.Equal(expectedData.Value, actualData.Value);
                return;
            case (PlistDate expectedDate, PlistDate actualDate):
                Assert.Equal(expectedDate.Value, actualDate.Value);
                return;
            default:
                Assert.Fail($"{path}: node kinds differ ({expected?.GetType().Name ?? "null"} vs {actual?.GetType().Name ?? "null"})");
                return;
        }
    }
}
