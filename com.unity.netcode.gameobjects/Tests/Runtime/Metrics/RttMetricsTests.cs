#if MULTIPLAYER_TOOLS
#if MULTIPLAYER_TOOLS_1_0_0_PRE_4

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Multiplayer.Tools.MetricTypes;
using UnityEngine.TestTools;
using Unity.Netcode.TestHelpers.Runtime;
using Unity.Netcode.TestHelpers.Runtime.Metrics;

namespace Unity.Netcode.RuntimeTests.Metrics
{
    /// <summary>
    /// Note: This is one way to easily identify each specific test.
    /// Since the test only tested 1 and then 2 clients, I made this
    /// and enum, but you can always remove the enum in the constructor,
    /// replace it with an int, and then test from 1 to 9 clients.
    /// Just an example of how you can accomplish the same task using
    /// the NetcodeIntegrationTest
    /// </summary>
    [TestFixture(ClientCount.OneClient)]
    [TestFixture(ClientCount.TwoClients)]
    internal class RttMetricsTests : NetcodeIntegrationTest
    {
        protected override int NumberOfClients => m_ClientCount;

        public enum ClientCount
        {
            OneClient,
            TwoClients
        }

        private int m_ClientCount;

        public RttMetricsTests(ClientCount numberOfClients)
        {
            m_ClientCount = numberOfClients == ClientCount.OneClient ? 1 : 2;
        }

        /// <summary>
        /// Note: We are using the OnOneTimeSetup to select the transport to use for
        /// this test set.
        /// </summary>
        protected override void OnOneTimeSetup()
        {
#if UTP_ADAPTER
            m_NetworkTransport = NetcodeIntegrationTestHelpers.InstanceTransport.UTP;
#else
            m_NetworkTransport = NetcodeIntegrationTestHelpers.InstanceTransport.SIP;
#endif
        }

        [UnityTest]
        public IEnumerator TrackRttMetricServerToClient()
        {
            var waitForMetricValues = new WaitForGaugeMetricValues((m_ServerNetworkManager.NetworkMetrics as NetworkMetrics).Dispatcher, NetworkMetricTypes.RttToServer);

            using (var writer = new FastBufferWriter(sizeof(uint), Allocator.Temp))
            {
                writer.WriteValueSafe(1337);
                m_ServerNetworkManager.CustomMessagingManager.SendUnnamedMessageToAll(writer);
            }

            yield return WaitForConditionOrTimeOut(() => waitForMetricValues.MetricFound());
            Assert.False(s_GloabalTimeoutHelper.TimedOut, $"{nameof(TrackRttMetricServerToClient)} timed out waiting for metric to be found for {m_ClientCount} clients!");

            var rttValue = waitForMetricValues.AssertMetricValueHaveBeenFound();
            Assert.AreEqual(0f, rttValue);
        }

        [UnityTest]
        public IEnumerator TrackRttMetricClientToServer()
        {
            var clientGaugeMetricValues = new List<WaitForGaugeMetricValues>();
            foreach (var client in m_ClientNetworkManagers)
            {
                clientGaugeMetricValues.Add(new WaitForGaugeMetricValues((client.NetworkMetrics as NetworkMetrics).Dispatcher, NetworkMetricTypes.RttToServer, metric => metric > 0f));
            }

            using (var writer = new FastBufferWriter(sizeof(uint), Allocator.Temp))
            {
                writer.WriteValueSafe(1337);
                m_ServerNetworkManager.CustomMessagingManager.SendUnnamedMessageToAll(writer);
            }

            yield return WaitForConditionOrTimeOut(() => clientGaugeMetricValues.Where((c) => c.MetricFound()).Count() == NumberOfClients);
            Assert.False(s_GloabalTimeoutHelper.TimedOut, $"{nameof(TrackRttMetricClientToServer)} timed out waiting for metric to be found for {m_ClientCount} clients!");

            foreach (var clientGaugeMetricValue in clientGaugeMetricValues)
            {
                var rttValue = clientGaugeMetricValue.AssertMetricValueHaveBeenFound();
                Assert.That(rttValue, Is.GreaterThanOrEqualTo(1f));
            }
        }
    }
}
#endif
#endif
