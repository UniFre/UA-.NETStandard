/* ========================================================================
 * Copyright (c) 2005-2020 The OPC Foundation, Inc. All rights reserved.
 *
 * OPC Foundation MIT License 1.00
 * 
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 * 
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 *
 * The complete license agreement can be found here:
 * http://opcfoundation.org/License/MIT/1.00/
 * ======================================================================*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using NUnit.Framework;
using Opc.Ua.Server.Tests;
using Quickstarts.ReferenceServer;
using Assert = NUnit.Framework.Legacy.ClassicAssert;


namespace Opc.Ua.Client.Tests
{
    /// <summary>
    /// Test Client Services.
    /// </summary>
    [TestFixture, Category("Client")]
    [SetCulture("en-us"), SetUICulture("en-us")]
    public class DurableSubscriptionTest : ClientTestFramework
    {
        private readonly string m_subscriptionTestXml = Path.Combine(Path.GetTempPath(), "SubscriptionTest.xml");
        public readonly uint MillisecondsPerHour = 3600 * 1000;


        #region Test Setup
        /// <summary>
        /// Set up a Server and a Client instance.
        /// </summary>
        [OneTimeSetUp]
        public new Task OneTimeSetUp()
        {
            // the tests can be run against server specified in .runsettings
            SupportsExternalServerUrl = true;
            // create a new session for every test
            SingleSession = false;
            MaxChannelCount = 1000;
            return base.OneTimeSetUpAsync(writer: null, securityNone: true);
        }

        override public async Task CreateReferenceServerFixture(
            bool enableTracing,
            bool disableActivityLogging,
            bool securityNone,
            TextWriter writer)
        {
            {
                // start Ref server
                ServerFixture = new ServerFixture<ReferenceServer>(enableTracing, disableActivityLogging) {
                    UriScheme = UriScheme,
                    SecurityNone = securityNone,
                    AutoAccept = true,
                    AllNodeManagers = true,
                    OperationLimits = true,
                    DurableSubscriptionsEnabled = true
                };
            }

            if (writer != null)
            {
                ServerFixture.TraceMasks = Utils.TraceMasks.Error | Utils.TraceMasks.Security;
            }

            await ServerFixture.LoadConfiguration(PkiRoot).ConfigureAwait(false);
            ServerFixture.Config.TransportQuotas.MaxMessageSize = TransportQuotaMaxMessageSize;
            ServerFixture.Config.TransportQuotas.MaxByteStringLength =
            ServerFixture.Config.TransportQuotas.MaxStringLength = TransportQuotaMaxStringLength;
            ServerFixture.Config.ServerConfiguration.UserTokenPolicies.Add(new UserTokenPolicy(UserTokenType.UserName));
            ServerFixture.Config.ServerConfiguration.UserTokenPolicies.Add(new UserTokenPolicy(UserTokenType.Certificate));
            ServerFixture.Config.ServerConfiguration.UserTokenPolicies.Add(
                new UserTokenPolicy(UserTokenType.IssuedToken) { IssuedTokenType = Opc.Ua.Profiles.JwtUserToken });

            ReferenceServer = await ServerFixture.StartAsync(writer ?? TestContext.Out).ConfigureAwait(false);
            ReferenceServer.TokenValidator = this.TokenValidator;
            ServerFixturePort = ServerFixture.Port;
        }


        /// <summary>
        /// Tear down the Server and the Client.
        /// </summary>
        [OneTimeTearDown]
        public new Task OneTimeTearDownAsync()
        {
            return base.OneTimeTearDownAsync();
        }

        /// <summary>
        /// Test setup.
        /// </summary>
        [SetUp]
        public new Task SetUp()
        {
            return MySetUp();
        }

        public async Task MySetUp()
        {
            if (!SingleSession)
            {
                try
                {
                    Session = await ClientFixture.ConnectAsync(ServerUrl,
                        SecurityPolicies.Basic256Sha256,
                        null,
                        new UserIdentity("sysadmin", "demo")).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Assert.Ignore($"OneTimeSetup failed to create session, tests skipped. Error: {e.Message}");
                }
            }
            if (ServerFixture == null)
            {
                ClientFixture.SetTraceOutput(TestContext.Out);
            }
            else
            {
                ServerFixture.SetTraceOutput(TestContext.Out);
            }
        }

        /// <summary>
        /// Test teardown.
        /// </summary>
        [TearDown]
        public new Task TearDown()
        {
            return base.TearDown();
        }
        #endregion

        #region Tests

        [Test, Order(99)]
        [TestCase(900, 100u, 100u, 10000u, 3600u, 83442u, TestName = "Test Lifetime Over Maximum")]
        [TestCase(900, 100u, 100u, 0u, 3600u, 83442u, TestName = "Test Lifetime Zero")]
        [TestCase(1200, 100u, 100u, 1u, 1u, 3000u, TestName = "Test Lifetime One")]
        [TestCase(60000, 183u, 61u, 1u, 1u, 60u, TestName = "Test Lifetime Reduce Count",
            Description = "Expected MaxLifetimeCount matches what the demo server does")]

        public void TestLifetime(int publishingInterval,
            uint keepAliveCount,
            uint lifetimeCount,
            uint requestedHours,
            uint expectedHours,
            uint expectedLifetime)
        {
            TestableSubscription subscription = new TestableSubscription(Session.DefaultSubscription);

            subscription.KeepAliveCount = keepAliveCount;
            subscription.LifetimeCount = lifetimeCount;
            subscription.PublishingInterval = publishingInterval;

            Assert.True(Session.AddSubscription(subscription));
            subscription.Create();

            Dictionary<string, NodeId> desiredNodeIds = GetDesiredNodeIds(subscription.Id);
            Dictionary<string, object> initialValues = GetValues(desiredNodeIds);

            uint revisedLifetimeInHours = 0;
            Assert.True(subscription.SetSubscriptionDurable(requestedHours, out revisedLifetimeInHours));

            Assert.AreEqual(expectedHours, revisedLifetimeInHours);

            Dictionary<string, object> modifiedValues = GetValues(desiredNodeIds);

            DataValue maxLifetimeCountValue = modifiedValues["MaxLifetimeCount"] as DataValue;
            Assert.IsNotNull(maxLifetimeCountValue);
            Assert.IsNotNull(maxLifetimeCountValue.Value);
            Assert.AreEqual(expectedLifetime,
                Convert.ToUInt32(maxLifetimeCountValue.Value));

            Assert.True(Session.RemoveSubscription(subscription));
        }

        [Test, Order(110)]
        [TestCase(0u, 1u, 1u, false, TestName = "QueueSize 0")]
        [TestCase(101u, 101u, 102u, false, TestName = "QueueSize over standard subscripion limit")]
        [TestCase(9999u, 1000u, 1000u, false, TestName = "QueueSize over durable limit")]
        [TestCase(0u, 1000u, 1u, true, TestName = "QueueSize 0 Event MI")]
        [TestCase(1001u, 1001u, 1002u, true, TestName = "QueueSize over standard subscripion limit Event MI")]
        [TestCase(99999u, 10000u, 10000u, true, TestName = "QueueSize over durable limit, Event MI")]
        public async Task TestRevisedQueueSizeAsync(
            uint queueSize,
            uint expectedRevisedQueueSize,
            uint expectedModifiedQueueSize,
            bool useEventMI)
        {
            var subscription = await CreateDurableSubscriptionAsync();

            MonitoredItem mi;
            if (useEventMI)
            {
                mi = CreateEventMonitoredItem(queueSize);
            }
            else
            {
                mi = new MonitoredItem {
                    AttributeId = Attributes.Value,
                    StartNodeId = VariableIds.Server_ServerStatus_CurrentTime,
                    MonitoringMode = MonitoringMode.Reporting,
                    Handle = 1,
                    SamplingInterval = 500,
                    Filter = null,
                    DiscardOldest = true,
                    QueueSize = queueSize,
                };
            }


            subscription.AddItem(mi);

            var result = await subscription.CreateItemsAsync();
            Assert.That(ServiceResult.IsGood(result.First().Status.Error), Is.True);
            Assert.That(result.First().Status.QueueSize, Is.EqualTo(expectedRevisedQueueSize));

            mi.QueueSize = queueSize + 1;

            var resultModify = await subscription.ModifyItemsAsync();
            Assert.That(ServiceResult.IsGood(resultModify.First().Status.Error), Is.True);
            Assert.That(resultModify.First().Status.QueueSize, Is.EqualTo(expectedModifiedQueueSize));


            Assert.True(subscription.GetMonitoredItems(out _, out _));

            Assert.True(await Session.RemoveSubscriptionAsync(subscription));
        }

        [Test]
        public void SetSubscriptionDurableFailsWhenMIExists()
        {
            var subscription = new TestableSubscription(Session.DefaultSubscription) {
                KeepAliveCount = 100u,
                LifetimeCount = 100u,
                PublishingInterval = 900
            };

            Assert.True(Session.AddSubscription(subscription));
            subscription.Create();

            uint id = subscription.Id;

            var mi = new MonitoredItem {
                AttributeId = Attributes.Value,
                StartNodeId = VariableIds.Server_ServerStatus_CurrentTime,
                MonitoringMode = MonitoringMode.Reporting,
                Handle = 1,
                SamplingInterval = 500,
                Filter = null,
                DiscardOldest = true,
                QueueSize = 1,
            };

            subscription.AddItem(mi);

            var result = subscription.CreateItems();
            Assert.That(ServiceResult.IsGood(result.First().Status.Error), Is.True);

            Assert.Throws<ServiceResultException>(() => Session.Call(ObjectIds.Server,
                    MethodIds.Server_SetSubscriptionDurable,
                    id, 1));

            Assert.True(Session.RemoveSubscription(subscription));
        }

        [Test]
        public void SetSubscriptionDurableFailsWhenSubscriptionDoesNotExist()
        {
            var subscription = new TestableSubscription(Session.DefaultSubscription) {
                KeepAliveCount = 100u,
                LifetimeCount = 100u,
                PublishingInterval = 900
            };

            Assert.True(Session.AddSubscription(subscription));
            subscription.Create();

            uint id = subscription.Id;

            Assert.True(Session.RemoveSubscription(subscription));

            Assert.Throws<ServiceResultException>(() => Session.Call(ObjectIds.Server,
                    MethodIds.Server_SetSubscriptionDurable,
                    id, 1));
        }

        #endregion

        #region Helpers
        private async Task<TestableSubscription> CreateDurableSubscriptionAsync()
        {
            var subscription = new TestableSubscription(Session.DefaultSubscription) {
                KeepAliveCount = 100u,
                LifetimeCount = 100u,
                PublishingInterval = 900
            };

            Assert.True(Session.AddSubscription(subscription));
            subscription.Create();

            (bool success, _) = await subscription.SetSubscriptionDurableAsync(1);
            Assert.True(success);

            return subscription;
        }
        private Dictionary<string, NodeId> GetDesiredNodeIds(uint subscriptionId)
        {
            Dictionary<string, NodeId> desiredNodeIds = new Dictionary<string, NodeId>();

            NodeId serverDiags = new NodeId(Variables.Server_ServerDiagnostics_SubscriptionDiagnosticsArray);
            ReferenceDescriptionCollection references;

            NodeId monitoredItemCountNodeId = null;
            NodeId maxLifetimeCountNodeId = null;
            NodeId maxKeepAliveCountNodeId = null;
            NodeId currentLifetimeCountNodeId = null;
            NodeId publishingIntervalNodeId = null;

            Session.Browse(null, null,
                serverDiags,
                0u, BrowseDirection.Forward,
                ReferenceTypeIds.HierarchicalReferences,
                true, 0,
                out var continuationPoint,
                out references);

            Assert.NotNull(references);
            Assert.Greater(references.Count, 0);

            foreach (ReferenceDescription reference in references)
            {
                if (reference.BrowseName.Name == subscriptionId.ToString())
                {
                    ReferenceDescriptionCollection desiredReferences;

                    Session.Browse(null, null,
                        ((NodeId)reference.NodeId),
                        0u, BrowseDirection.Forward,
                        ReferenceTypeIds.HierarchicalReferences,
                        true, 0,
                        out var anotherContinuationPoint,
                        out desiredReferences);

                    Assert.NotNull(desiredReferences);
                    Assert.Greater(desiredReferences.Count, 0);

                    foreach (ReferenceDescription referenceDescription in desiredReferences)
                    {
                        if (referenceDescription.BrowseName.Name.Equals("MonitoredItemCount",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            monitoredItemCountNodeId = ((NodeId)referenceDescription.NodeId);
                        }
                        else if (referenceDescription.BrowseName.Name.Equals("MaxLifetimeCount",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            maxLifetimeCountNodeId = ((NodeId)referenceDescription.NodeId);

                        }
                        else if (referenceDescription.BrowseName.Name.Equals("MaxKeepAliveCount",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            maxKeepAliveCountNodeId = ((NodeId)referenceDescription.NodeId);

                        }
                        else if (referenceDescription.BrowseName.Name.Equals("CurrentLifetimeCount",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            currentLifetimeCountNodeId = ((NodeId)referenceDescription.NodeId);
                        }
                        else if (referenceDescription.BrowseName.Name.Equals("PublishingInterval",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            publishingIntervalNodeId = ((NodeId)referenceDescription.NodeId);
                        }
                    }
                }
                break;
            }

            Assert.IsNotNull(monitoredItemCountNodeId);
            Assert.IsNotNull(maxLifetimeCountNodeId);
            Assert.IsNotNull(maxKeepAliveCountNodeId);
            Assert.IsNotNull(currentLifetimeCountNodeId);
            Assert.IsNotNull(publishingIntervalNodeId);

            desiredNodeIds.Add("MonitoredItemCount", monitoredItemCountNodeId);
            desiredNodeIds.Add("MaxLifetimeCount", maxLifetimeCountNodeId);
            desiredNodeIds.Add("MaxKeepAliveCount", maxKeepAliveCountNodeId);
            desiredNodeIds.Add("CurrentLifetimeCount", currentLifetimeCountNodeId);
            desiredNodeIds.Add("PublishingInterval", publishingIntervalNodeId);

            return desiredNodeIds;
        }

        private Dictionary<string, object> GetValues(Dictionary<string, NodeId> ids)
        {
            Dictionary<string, object> values = new Dictionary<string, object>();

            foreach (KeyValuePair<string, NodeId> id in ids)
            {
                values.Add(id.Key, Session.ReadValue(id.Value));
                Debug.WriteLine($"{id.Key}: {values[id.Key]}");
            }

            return values;
        }

        private static MonitoredItem CreateEventMonitoredItem(uint queueSize)
        {
            var whereClause = new ContentFilter();

            whereClause.Push(FilterOperator.Equals, new FilterOperand[] {
                new SimpleAttributeOperand() {
                    AttributeId = Attributes.Value,
                    TypeDefinitionId = ObjectTypeIds.BaseEventType,
                    BrowsePath = new QualifiedNameCollection(new QualifiedName[] { "EventType" })
                },
                new LiteralOperand {
                    Value = new Variant(new NodeId(ObjectTypeIds.BaseEventType))
                }
            });

            var mi = new MonitoredItem() {
                AttributeId = Attributes.EventNotifier,
                StartNodeId = ObjectIds.Server,
                MonitoringMode = MonitoringMode.Reporting,
                Handle = 1,
                SamplingInterval = -1,
                Filter =
                        new EventFilter {
                            SelectClauses = new SimpleAttributeOperandCollection(
                            new SimpleAttributeOperand[] {
                                new SimpleAttributeOperand{
                                    AttributeId = Attributes.Value,
                                    TypeDefinitionId = ObjectTypeIds.BaseEventType,
                                    BrowsePath = new QualifiedNameCollection(new QualifiedName[] { BrowseNames.Message})
                                }
                            }),
                            WhereClause = whereClause,
                        },
                DiscardOldest = true,
                QueueSize = queueSize
            };
            return mi;
        }

        #endregion
    }
}
