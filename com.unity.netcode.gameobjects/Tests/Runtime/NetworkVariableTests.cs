using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.TestTools;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode.TestHelpers.Runtime;
using Random = UnityEngine.Random;

namespace Unity.Netcode.RuntimeTests
{
    public struct TestStruct : INetworkSerializable, IEquatable<TestStruct>
    {
        public uint SomeInt;
        public bool SomeBool;
        public static bool NetworkSerializeCalledOnWrite;
        public static bool NetworkSerializeCalledOnRead;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            if (serializer.IsReader)
            {
                NetworkSerializeCalledOnRead = true;
            }
            else
            {
                NetworkSerializeCalledOnWrite = true;
            }
            serializer.SerializeValue(ref SomeInt);
            serializer.SerializeValue(ref SomeBool);
        }

        public bool Equals(TestStruct other)
        {
            return SomeInt == other.SomeInt && SomeBool == other.SomeBool;
        }

        public override bool Equals(object obj)
        {
            return obj is TestStruct other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)SomeInt * 397) ^ SomeBool.GetHashCode();
            }
        }
    }

    public class NetworkVariableTest : NetworkBehaviour
    {
        public readonly NetworkVariable<int> TheScalar = new NetworkVariable<int>();
        public readonly NetworkList<int> TheList = new NetworkList<int>();
        public readonly NetworkList<FixedString128Bytes> TheLargeList = new NetworkList<FixedString128Bytes>();

        public readonly NetworkVariable<FixedString32Bytes> FixedString32 = new NetworkVariable<FixedString32Bytes>();

        private void ListChanged(NetworkListEvent<int> e)
        {
            ListDelegateTriggered = true;
        }

        public void Awake()
        {
            TheList.OnListChanged += ListChanged;
        }

        public readonly NetworkVariable<TestStruct> TheStruct = new NetworkVariable<TestStruct>();
        public readonly NetworkList<TestStruct> TheListOfStructs = new NetworkList<TestStruct>();

        public bool ListDelegateTriggered;

        public override void OnNetworkSpawn()
        {
            if (!IsServer)
            {
                NetworkVariableTests.ClientNetworkVariableTestSpawned(this);
            }
            base.OnNetworkSpawn();
        }
    }

    [TestFixture(true)]
    [TestFixture(false)]
    public class NetworkVariableTests : NetcodeIntegrationTest
    {
        private const string k_FixedStringTestValue = "abcdefghijklmnopqrstuvwxyz";
        protected override int NumberOfClients => 2;

        private const uint k_TestUInt = 0x12345678;

        private const int k_TestVal1 = 111;
        private const int k_TestVal2 = 222;
        private const int k_TestVal3 = 333;

        private const int k_TestKey1 = 0x0f0f;

        private static List<NetworkVariableTest> s_ClientNetworkVariableTestInstances = new List<NetworkVariableTest>();
        public static void ClientNetworkVariableTestSpawned(NetworkVariableTest networkVariableTest)
        {
            s_ClientNetworkVariableTestInstances.Add(networkVariableTest);
        }

        // Player1 component on the server
        private NetworkVariableTest m_Player1OnServer;

        // Player1 component on client1
        private NetworkVariableTest m_Player1OnClient1;

        private NetworkListTestPredicate m_NetworkListPredicateHandler;

        private bool m_EnsureLengthSafety;

        public NetworkVariableTests(bool ensureLengthSafety)
        {
            m_EnsureLengthSafety = ensureLengthSafety;
        }

        protected override bool CanStartServerAndClients()
        {
            return false;
        }

        /// <summary>
        /// This is an adjustment to how the server and clients are started in order
        /// to avoid timing issues when running in a stand alone test runner build.
        /// </summary>
        private IEnumerator InitializeServerAndClients(bool useHost)
        {
            s_ClientNetworkVariableTestInstances.Clear();
            m_PlayerPrefab.AddComponent<NetworkVariableTest>();

            m_ServerNetworkManager.NetworkConfig.EnsureNetworkVariableLengthSafety = m_EnsureLengthSafety;
            m_ServerNetworkManager.NetworkConfig.PlayerPrefab = m_PlayerPrefab;
            foreach (var client in m_ClientNetworkManagers)
            {
                client.NetworkConfig.EnsureNetworkVariableLengthSafety = m_EnsureLengthSafety;
                client.NetworkConfig.PlayerPrefab = m_PlayerPrefab;
            }

            Assert.True(NetcodeIntegrationTestHelpers.Start(useHost, m_ServerNetworkManager, m_ClientNetworkManagers), "Failed to start server and client instances");

            RegisterSceneManagerHandler();

            // Wait for connection on client and server side
            yield return WaitForClientsConnectedOrTimeOut();

            // These are the *SERVER VERSIONS* of the *CLIENT PLAYER 1 & 2*
            var result = new NetcodeIntegrationTestHelpers.ResultWrapper<NetworkObject>();

            yield return NetcodeIntegrationTestHelpers.GetNetworkObjectByRepresentation(
                x => x.IsPlayerObject && x.OwnerClientId == m_ClientNetworkManagers[0].LocalClientId,
                m_ServerNetworkManager, result);

            // Assign server-side client's player
            m_Player1OnServer = result.Result.GetComponent<NetworkVariableTest>();

            // This is client1's view of itself
            yield return NetcodeIntegrationTestHelpers.GetNetworkObjectByRepresentation(
                x => x.IsPlayerObject && x.OwnerClientId == m_ClientNetworkManagers[0].LocalClientId,
                m_ClientNetworkManagers[0], result);

            // Assign client-side local player
            m_Player1OnClient1 = result.Result.GetComponent<NetworkVariableTest>();

            m_Player1OnServer.TheList.Clear();

            if (m_Player1OnServer.TheList.Count > 0)
            {
                throw new Exception("at least one server network container not empty at start");
            }
            if (m_Player1OnClient1.TheList.Count > 0)
            {
                throw new Exception("at least one client network container not empty at start");
            }

            var instanceCount = useHost ? NumberOfClients * 3 : NumberOfClients * 2;
            // Wait for the client-side to notify it is finished initializing and spawning.
            yield return WaitForConditionOrTimeOut(() => s_ClientNetworkVariableTestInstances.Count == instanceCount);

            Assert.False(s_GloabalTimeoutHelper.TimedOut, "Timed out waiting for all client NetworkVariableTest instances to register they have spawned!");

            yield return s_DefaultWaitForTick;
        }

        /// <summary>
        /// Runs generalized tests on all predefined NetworkVariable types
        /// </summary>
        [UnityTest]
        public IEnumerator AllNetworkVariableTypes([Values(true, false)] bool useHost)
        {
            // Create, instantiate, and host
            // This would normally go in Setup, but since every other test but this one
            //  uses NetworkManagerHelper, and it does its own NetworkManager setup / teardown,
            //  for now we put this within this one test until we migrate it to MIH
            Assert.IsTrue(NetworkManagerHelper.StartNetworkManager(out NetworkManager server, useHost ? NetworkManagerHelper.NetworkManagerOperatingMode.Host : NetworkManagerHelper.NetworkManagerOperatingMode.Server));

            Assert.IsTrue(server.IsHost == useHost, $"{nameof(useHost)} does not match the server.IsHost value!");

            Guid gameObjectId = NetworkManagerHelper.AddGameNetworkObject("NetworkVariableTestComponent");

            var networkVariableTestComponent = NetworkManagerHelper.AddComponentToObject<NetworkVariableTestComponent>(gameObjectId);

            NetworkManagerHelper.SpawnNetworkObject(gameObjectId);

            // Start Testing
            networkVariableTestComponent.EnableTesting = true;

            yield return WaitForConditionOrTimeOut(() => true == networkVariableTestComponent.IsTestComplete());
            Assert.IsFalse(s_GloabalTimeoutHelper.TimedOut, "Timed out waiting for the test to complete!");

            // Stop Testing
            networkVariableTestComponent.EnableTesting = false;

            Assert.IsTrue(networkVariableTestComponent.DidAllValuesChange());

            // Disable this once we are done.
            networkVariableTestComponent.gameObject.SetActive(false);

            // This would normally go in Teardown, but since every other test but this one
            //  uses NetworkManagerHelper, and it does its own NetworkManager setup / teardown,
            //  for now we put this within this one test until we migrate it to MIH
            NetworkManagerHelper.ShutdownNetworkManager();
        }

        [UnityTest]
        public IEnumerator ClientWritePermissionTest([Values(true, false)] bool useHost)
        {
            yield return InitializeServerAndClients(useHost);

            // client must not be allowed to write to a server auth variable
            Assert.Throws<InvalidOperationException>(() => m_Player1OnClient1.TheScalar.Value = k_TestVal1);
        }

        [UnityTest]
        public IEnumerator FixedString32Test([Values(true, false)] bool useHost)
        {
            yield return InitializeServerAndClients(useHost);
            m_Player1OnServer.FixedString32.Value = k_FixedStringTestValue;

            // Now wait for the client side version to be updated to k_FixedStringTestValue
            yield return WaitForConditionOrTimeOut(() => m_Player1OnClient1.FixedString32.Value == k_FixedStringTestValue);
            Assert.IsFalse(s_GloabalTimeoutHelper.TimedOut, "Timed out waiting for client-side NetworkVariable to update!");
        }

        [UnityTest]
        public IEnumerator NetworkListAdd([Values(true, false)] bool useHost)
        {
            yield return InitializeServerAndClients(useHost);
            m_NetworkListPredicateHandler = new NetworkListTestPredicate(m_Player1OnServer, m_Player1OnClient1, NetworkListTestPredicate.NetworkListTestStates.Add, 10);
            yield return WaitForConditionOrTimeOut(m_NetworkListPredicateHandler);
        }

        [UnityTest]
        public IEnumerator WhenListContainsManyLargeValues_OverflowExceptionIsNotThrown([Values(true, false)] bool useHost)
        {
            yield return InitializeServerAndClients(useHost);
            m_NetworkListPredicateHandler = new NetworkListTestPredicate(m_Player1OnServer, m_Player1OnClient1, NetworkListTestPredicate.NetworkListTestStates.ContainsLarge, 20);
            yield return WaitForConditionOrTimeOut(m_NetworkListPredicateHandler);
        }

        [UnityTest]
        public IEnumerator NetworkListContains([Values(true, false)] bool useHost)
        {
            // Re-use the NetworkListAdd to initialize the server and client as well as make sure the list is populated
            yield return NetworkListAdd(useHost);

            // Now test the NetworkList.Contains method
            m_NetworkListPredicateHandler.SetNetworkListTestState(NetworkListTestPredicate.NetworkListTestStates.Contains);
            yield return WaitForConditionOrTimeOut(m_NetworkListPredicateHandler);
        }

        [UnityTest]
        public IEnumerator NetworkListRemove([Values(true, false)] bool useHost)
        {
            // Re-use the NetworkListAdd to initialize the server and client as well as make sure the list is populated
            yield return NetworkListAdd(useHost);

            // Remove two entries by index
            m_Player1OnServer.TheList.Remove(3);
            m_Player1OnServer.TheList.Remove(5);

            // Really just verifies the data at this point
            m_NetworkListPredicateHandler.SetNetworkListTestState(NetworkListTestPredicate.NetworkListTestStates.VerifyData);
            yield return WaitForConditionOrTimeOut(m_NetworkListPredicateHandler);
        }

        [UnityTest]
        public IEnumerator NetworkListInsert([Values(true, false)] bool useHost)
        {
            // Re-use the NetworkListAdd to initialize the server and client as well as make sure the list is populated
            yield return NetworkListAdd(useHost);

            // Now randomly insert a random value entry
            m_Player1OnServer.TheList.Insert(Random.Range(0, 9), Random.Range(1, 99));

            // Verify the element count and values on the client matches the server
            m_NetworkListPredicateHandler.SetNetworkListTestState(NetworkListTestPredicate.NetworkListTestStates.VerifyData);
            yield return WaitForConditionOrTimeOut(m_NetworkListPredicateHandler);
        }

        [UnityTest]
        public IEnumerator NetworkListIndexOf([Values(true, false)] bool useHost)
        {
            // Re-use the NetworkListAdd to initialize the server and client as well as make sure the list is populated
            yield return NetworkListAdd(useHost);

            m_NetworkListPredicateHandler.SetNetworkListTestState(NetworkListTestPredicate.NetworkListTestStates.IndexOf);
            yield return WaitForConditionOrTimeOut(m_NetworkListPredicateHandler);
        }

        [UnityTest]
        public IEnumerator NetworkListValueUpdate([Values(true, false)] bool useHost)
        {
            var testSucceeded = false;
            yield return InitializeServerAndClients(useHost);
            // Add 1 element value and verify it is the same on the client
            m_NetworkListPredicateHandler = new NetworkListTestPredicate(m_Player1OnServer, m_Player1OnClient1, NetworkListTestPredicate.NetworkListTestStates.Add, 1);
            yield return WaitForConditionOrTimeOut(m_NetworkListPredicateHandler);

            // Setup our original and
            var previousValue = m_Player1OnServer.TheList[0];
            var updatedValue = previousValue + 10;

            // Callback that verifies the changed event occurred and that the original and new values are correct
            void TestValueUpdatedCallback(NetworkListEvent<int> changedEvent)
            {
                testSucceeded = changedEvent.PreviousValue == previousValue &&
                                changedEvent.Value == updatedValue;
            }

            // Subscribe to the OnListChanged event on the client side and
            m_Player1OnClient1.TheList.OnListChanged += TestValueUpdatedCallback;
            m_Player1OnServer.TheList[0] = updatedValue;

            // Wait until we know the client side matches the server side before checking if the callback was a success
            m_NetworkListPredicateHandler.SetNetworkListTestState(NetworkListTestPredicate.NetworkListTestStates.VerifyData);
            yield return WaitForConditionOrTimeOut(m_NetworkListPredicateHandler);

            Assert.That(testSucceeded);
            m_Player1OnClient1.TheList.OnListChanged -= TestValueUpdatedCallback;
        }

        [UnityTest]
        public IEnumerator NetworkListRemoveAt([Values(true, false)] bool useHost)
        {
            // Re-use the NetworkListAdd to initialize the server and client as well as make sure the list is populated
            yield return NetworkListAdd(useHost);

            // Randomly remove a few entries
            m_Player1OnServer.TheList.RemoveAt(Random.Range(0, m_Player1OnServer.TheList.Count - 1));
            m_Player1OnServer.TheList.RemoveAt(Random.Range(0, m_Player1OnServer.TheList.Count - 1));
            m_Player1OnServer.TheList.RemoveAt(Random.Range(0, m_Player1OnServer.TheList.Count - 1));

            // Verify the element count and values on the client matches the server
            m_NetworkListPredicateHandler.SetNetworkListTestState(NetworkListTestPredicate.NetworkListTestStates.VerifyData);
            yield return WaitForConditionOrTimeOut(m_NetworkListPredicateHandler);
        }

        [UnityTest]
        public IEnumerator NetworkListClear([Values(true, false)] bool useHost)
        {
            // Re-use the NetworkListAdd to initialize the server and client as well as make sure the list is populated
            yield return NetworkListAdd(useHost);
            m_Player1OnServer.TheList.Clear();
            // Verify the element count and values on the client matches the server
            m_NetworkListPredicateHandler.SetNetworkListTestState(NetworkListTestPredicate.NetworkListTestStates.VerifyData);
            yield return WaitForConditionOrTimeOut(m_NetworkListPredicateHandler);
        }

        [UnityTest]
        public IEnumerator TestNetworkVariableStruct([Values(true, false)] bool useHost)
        {
            yield return InitializeServerAndClients(useHost);

            bool VerifyStructure()
            {
                return m_Player1OnClient1.TheStruct.Value.SomeBool == m_Player1OnServer.TheStruct.Value.SomeBool &&
                    m_Player1OnClient1.TheStruct.Value.SomeInt == m_Player1OnServer.TheStruct.Value.SomeInt;
            }

            m_Player1OnServer.TheStruct.Value = new TestStruct() { SomeInt = k_TestUInt, SomeBool = false };
            m_Player1OnServer.TheStruct.SetDirty(true);

            // Wait for the client-side to notify it is finished initializing and spawning.
            yield return WaitForConditionOrTimeOut(VerifyStructure);
        }

        [UnityTest]
        public IEnumerator TestINetworkSerializableCallsNetworkSerialize([Values(true, false)] bool useHost)
        {
            yield return InitializeServerAndClients(useHost);
            TestStruct.NetworkSerializeCalledOnWrite = false;
            TestStruct.NetworkSerializeCalledOnRead = false;
            m_Player1OnServer.TheStruct.Value = new TestStruct() { SomeInt = k_TestUInt, SomeBool = false };

            static bool VerifyCallback() => TestStruct.NetworkSerializeCalledOnWrite && TestStruct.NetworkSerializeCalledOnRead;

            // Wait for the client-side to notify it is finished initializing and spawning.
            yield return WaitForConditionOrTimeOut(VerifyCallback);
        }

        #region COULD_BE_REMOVED
        [UnityTest]
        [Ignore("This is used several times already in the NetworkListPredicate")]
        // TODO: If we end up using the new suggested pattern, then delete this
        public IEnumerator NetworkListArrayOperator([Values(true, false)] bool useHost)
        {
            yield return NetworkListAdd(useHost);
        }

        [UnityTest]
        [Ignore("This is used several times already in the NetworkListPredicate")]
        // TODO: If we end up using the new suggested pattern, then delete this
        public IEnumerator NetworkListIEnumerator([Values(true, false)] bool useHost)
        {
            yield return InitializeServerAndClients(useHost);
            var correctVals = new int[3];
            correctVals[0] = k_TestVal1;
            correctVals[1] = k_TestVal2;
            correctVals[2] = k_TestVal3;

            m_Player1OnServer.TheList.Add(correctVals[0]);
            m_Player1OnServer.TheList.Add(correctVals[1]);
            m_Player1OnServer.TheList.Add(correctVals[2]);

            Assert.IsTrue(m_Player1OnServer.TheList.Count == 3);

            int index = 0;
            foreach (var val in m_Player1OnServer.TheList)
            {
                if (val != correctVals[index++])
                {
                    Assert.Fail();
                }
            }
        }
        #endregion


        protected override IEnumerator OnTearDown()
        {
            m_NetworkListPredicateHandler = null;
            yield return base.OnTearDown();
        }
    }

    /// <summary>
    /// Handles the more generic conditional logic for NetworkList tests
    /// which can be used with the <see cref="NetcodeIntegrationTest.WaitForConditionOrTimeOut"/>
    /// that accepts anything derived from the <see cref="ConditionalPredicateBase"/> class
    /// as a parameter.
    /// </summary>
    public class NetworkListTestPredicate : ConditionalPredicateBase
    {
        private const int k_MaxRandomValue = 1000;

        private Dictionary<NetworkListTestStates, Func<bool>> m_StateFunctions;

        // Player1 component on the Server
        private NetworkVariableTest m_Player1OnServer;

        // Player1 component on client1
        private NetworkVariableTest m_Player1OnClient1;

        private string m_TestStageFailedMessage;

        public enum NetworkListTestStates
        {
            Add,
            ContainsLarge,
            Contains,
            VerifyData,
            IndexOf,
        }

        private NetworkListTestStates m_NetworkListTestState;

        public void SetNetworkListTestState(NetworkListTestStates networkListTestState)
        {
            m_NetworkListTestState = networkListTestState;
        }

        /// <summary>
        /// Determines if the condition has been reached for the current NetworkListTestState
        /// </summary>
        protected override bool OnHasConditionBeenReached()
        {
            var isStateRegistered = m_StateFunctions.ContainsKey(m_NetworkListTestState);
            Assert.IsTrue(isStateRegistered);
            return m_StateFunctions[m_NetworkListTestState].Invoke();
        }

        /// <summary>
        /// Provides all information about the players for both sides for simplicity and informative sake.
        /// </summary>
        /// <returns></returns>
        private string ConditionFailedInfo()
        {
            return $"{m_NetworkListTestState} condition test failed:\n Server List Count: { m_Player1OnServer.TheList.Count} vs  Client List Count: { m_Player1OnClient1.TheList.Count}\n" +
                $"Server List Count: { m_Player1OnServer.TheLargeList.Count} vs  Client List Count: { m_Player1OnClient1.TheLargeList.Count}\n" +
                $"Server Delegate Triggered: {m_Player1OnServer.ListDelegateTriggered} | Client Delegate Triggered: {m_Player1OnClient1.ListDelegateTriggered}\n";
        }

        /// <summary>
        /// When finished, check if a time out occurred and if so assert and provide meaningful information to troubleshoot why
        /// </summary>
        protected override void OnFinished()
        {
            Assert.IsFalse(TimedOut, $"{nameof(NetworkListTestPredicate)} timed out waiting for the {m_NetworkListTestState} condition to be reached! \n" + ConditionFailedInfo());
        }

        // Uses the ArrayOperator and validates that on both sides the count and values are the same
        private bool OnVerifyData()
        {
            // Wait until both sides have the same number of elements
            if (m_Player1OnServer.TheList.Count != m_Player1OnClient1.TheList.Count)
            {
                return false;
            }

            // Check the client values against the server values to make sure they match
            for (int i = 0; i < m_Player1OnServer.TheList.Count; i++)
            {
                if (m_Player1OnServer.TheList[i] != m_Player1OnClient1.TheList[i])
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Verifies the data count, values, and that the ListDelegate on both sides was triggered
        /// </summary>
        private bool OnAdd()
        {
            bool wasTriggerred = m_Player1OnServer.ListDelegateTriggered && m_Player1OnClient1.ListDelegateTriggered;
            return wasTriggerred && OnVerifyData();
        }

        /// <summary>
        /// The current version of this test only verified the count of the large list, so that is what this does
        /// </summary>
        private bool OnContainsLarge()
        {
            return m_Player1OnServer.TheLargeList.Count == m_Player1OnClient1.TheLargeList.Count;
        }

        /// <summary>
        /// Tests NetworkList.Contains which also verifies all values are the same on both sides
        /// </summary>
        private bool OnContains()
        {
            // Wait until both sides have the same number of elements
            if (m_Player1OnServer.TheList.Count != m_Player1OnClient1.TheList.Count)
            {
                return false;
            }

            // Parse through all server values and use the NetworkList.Contains method to check if the value is in the list on the client side
            foreach (var serverValue in m_Player1OnServer.TheList)
            {
                if (!m_Player1OnClient1.TheList.Contains(serverValue))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Tests NetworkList.IndexOf and verifies that all values are aligned on both sides
        /// </summary>
        private bool OnIndexOf()
        {
            foreach (var serverSideValue in m_Player1OnServer.TheList)
            {
                var indexToTest = m_Player1OnServer.TheList.IndexOf(serverSideValue);
                if (indexToTest != m_Player1OnServer.TheList.IndexOf(serverSideValue))
                {
                    return false;
                }
            }
            return true;
        }

        public NetworkListTestPredicate(NetworkVariableTest player1OnServer, NetworkVariableTest player1OnClient1, NetworkListTestStates networkListTestState, int elementCount)
        {
            m_NetworkListTestState = networkListTestState;
            m_Player1OnServer = player1OnServer;
            m_Player1OnClient1 = player1OnClient1;
            m_StateFunctions = new Dictionary<NetworkListTestStates, Func<bool>>
            {
                { NetworkListTestStates.Add, OnAdd },
                { NetworkListTestStates.ContainsLarge, OnContainsLarge },
                { NetworkListTestStates.Contains, OnContains },
                { NetworkListTestStates.VerifyData, OnVerifyData },
                { NetworkListTestStates.IndexOf, OnIndexOf }
            };

            if (networkListTestState == NetworkListTestStates.ContainsLarge)
            {
                for (var i = 0; i < elementCount; ++i)
                {
                    m_Player1OnServer.TheLargeList.Add(new FixedString128Bytes());
                }
            }
            else
            {
                for (int i = 0; i < elementCount; i++)
                {
                    m_Player1OnServer.TheList.Add(Random.Range(0, k_MaxRandomValue));
                }
            }
        }
    }
}
