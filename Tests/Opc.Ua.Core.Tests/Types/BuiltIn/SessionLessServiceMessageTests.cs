using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Opc.Ua.Core.Tests.Types.BuiltIn
{
    /// <summary>
    /// Tests for the SessionLessServiceMessage Tests.
    /// </summary>
    [TestFixture, Category("BuiltIn")]
    [SetCulture("en-us"), SetUICulture("en-us")]
    [Parallelizable]
    public class SessionLessServiceMessageTests
    {
        [Test]
        [TestCase(JsonEncodingType.Compact)]
        [TestCase(JsonEncodingType.Reversible_Deprecated)]
        public void WhenServerUrisAreLessThanNamespacesShouldNotThrowAndMustReturnCorrectServerUris(JsonEncodingType encoding)
        {
            //arrange
            UInt32 urisVersion = 1234;
            var namespaceTable = new NamespaceTable(new List<string> { Namespaces.OpcUa, "http://bar", "http://foo" });
            var expectedServerUri = "http://foobar";
            var serverUris = new StringTable(new[] { Namespaces.OpcUa, expectedServerUri });
            var context = new ServiceMessageContext { NamespaceUris = namespaceTable, ServerUris = serverUris };
            string result;
            using (var jsonEncoder = new JsonEncoder(context, true))
            {
                var envelope = new SessionLessServiceMessage {
                    UrisVersion = urisVersion,
                    NamespaceUris = context.NamespaceUris,
                    ServerUris = context.ServerUris,
                    Message = null
                };

                //act and validate it does not throw
                Assert.DoesNotThrow(() => {
                    envelope.Encode(jsonEncoder);
                });

                result = jsonEncoder.CloseAndReturnText();
            }

            var jObject = JObject.Parse(result);
            Assert.IsNotNull(jObject);
            UInt32 version = jObject["UrisVersion"].ToObject<UInt32>();
            Assert.AreEqual(urisVersion, version);
            var serverUrisToken = jObject["ServerUris"];
            Assert.IsNotNull(serverUrisToken);
            var serverUrisEncoded = serverUrisToken.ToObject<string[]>();
            Assert.IsNotNull(serverUrisEncoded);
            Assert.AreEqual(1, serverUrisEncoded.Length);
            Assert.Contains(expectedServerUri, serverUrisEncoded);
        }
    }
}
