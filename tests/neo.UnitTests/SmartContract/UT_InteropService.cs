using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Cryptography;
using Neo.Cryptography.ECC;
using Neo.IO;
using Neo.Ledger;
using Neo.Network.P2P;
using Neo.Network.P2P.Payloads;
using Neo.SmartContract;
using Neo.SmartContract.Manifest;
using Neo.VM;
using Neo.VM.Types;
using Neo.Wallets;
using System;
using System.Linq;
using System.Text;

namespace Neo.UnitTests.SmartContract
{
    [TestClass]
    public partial class UT_InteropService
    {
        [TestInitialize]
        public void TestSetup()
        {
            TestBlockchain.InitializeMockNeoSystem();
        }

        [TestMethod]
        public void Runtime_GetNotifications_Test()
        {
            UInt160 scriptHash2;
            var snapshot = Blockchain.Singleton.GetSnapshot();

            using (var script = new ScriptBuilder())
            {
                // Notify method

                script.Emit(OpCode.SWAP, OpCode.NEWARRAY, OpCode.SWAP);
                script.EmitSysCall(ApplicationEngine.System_Runtime_Notify);

                // Add return

                script.EmitPush(true);
                script.Emit(OpCode.RET);

                // Mock contract

                scriptHash2 = script.ToArray().ToScriptHash();

                snapshot.Contracts.Delete(scriptHash2);
                snapshot.Contracts.Add(scriptHash2, new ContractState()
                {
                    Script = script.ToArray(),
                    Manifest = TestUtils.CreateManifest(scriptHash2, "test", ContractParameterType.Any, ContractParameterType.Integer, ContractParameterType.Integer),
                });
            }

            // Wrong length

            using (var engine = ApplicationEngine.Create(TriggerType.Application, null, snapshot))
            using (var script = new ScriptBuilder())
            {
                // Retrive

                script.EmitPush(1);
                script.EmitSysCall(ApplicationEngine.System_Runtime_GetNotifications);

                // Execute

                engine.LoadScript(script.ToArray());

                Assert.AreEqual(VMState.FAULT, engine.Execute());
            }

            // All test

            using (var engine = ApplicationEngine.Create(TriggerType.Application, null, snapshot))
            using (var script = new ScriptBuilder())
            {
                // Notification

                script.EmitPush(0);
                script.Emit(OpCode.NEWARRAY);
                script.EmitPush("testEvent1");
                script.EmitSysCall(ApplicationEngine.System_Runtime_Notify);

                // Call script

                script.EmitAppCall(scriptHash2, "test", "testEvent2", 1);

                // Drop return

                script.Emit(OpCode.DROP);

                // Receive all notifications

                script.Emit(OpCode.PUSHNULL);
                script.EmitSysCall(ApplicationEngine.System_Runtime_GetNotifications);

                // Execute

                engine.LoadScript(script.ToArray());
                var currentScriptHash = engine.EntryScriptHash;

                Assert.AreEqual(VMState.HALT, engine.Execute());
                Assert.AreEqual(1, engine.ResultStack.Count);
                Assert.AreEqual(2, engine.Notifications.Count);

                Assert.IsInstanceOfType(engine.ResultStack.Peek(), typeof(VM.Types.Array));

                var array = (VM.Types.Array)engine.ResultStack.Pop();

                // Check syscall result

                AssertNotification(array[1], scriptHash2, "testEvent2");
                AssertNotification(array[0], currentScriptHash, "testEvent1");

                // Check notifications

                Assert.AreEqual(scriptHash2, engine.Notifications[1].ScriptHash);
                Assert.AreEqual("testEvent2", engine.Notifications[1].EventName);

                Assert.AreEqual(currentScriptHash, engine.Notifications[0].ScriptHash);
                Assert.AreEqual("testEvent1", engine.Notifications[0].EventName);
            }

            // Script notifications

            using (var engine = ApplicationEngine.Create(TriggerType.Application, null, snapshot))
            using (var script = new ScriptBuilder())
            {
                // Notification

                script.EmitPush(0);
                script.Emit(OpCode.NEWARRAY);
                script.EmitPush("testEvent1");
                script.EmitSysCall(ApplicationEngine.System_Runtime_Notify);

                // Call script

                script.EmitAppCall(scriptHash2, "test", "testEvent2", 1);

                // Drop return

                script.Emit(OpCode.DROP);

                // Receive all notifications

                script.EmitPush(scriptHash2.ToArray());
                script.EmitSysCall(ApplicationEngine.System_Runtime_GetNotifications);

                // Execute

                engine.LoadScript(script.ToArray());
                var currentScriptHash = engine.EntryScriptHash;

                Assert.AreEqual(VMState.HALT, engine.Execute());
                Assert.AreEqual(1, engine.ResultStack.Count);
                Assert.AreEqual(2, engine.Notifications.Count);

                Assert.IsInstanceOfType(engine.ResultStack.Peek(), typeof(VM.Types.Array));

                var array = (VM.Types.Array)engine.ResultStack.Pop();

                // Check syscall result

                AssertNotification(array[0], scriptHash2, "testEvent2");

                // Check notifications

                Assert.AreEqual(scriptHash2, engine.Notifications[1].ScriptHash);
                Assert.AreEqual("testEvent2", engine.Notifications[1].EventName);

                Assert.AreEqual(currentScriptHash, engine.Notifications[0].ScriptHash);
                Assert.AreEqual("testEvent1", engine.Notifications[0].EventName);
            }

            // Clean storage

            snapshot.Contracts.Delete(scriptHash2);
        }

        private void AssertNotification(StackItem stackItem, UInt160 scriptHash, string notification)
        {
            Assert.IsInstanceOfType(stackItem, typeof(VM.Types.Array));

            var array = (VM.Types.Array)stackItem;
            Assert.AreEqual(3, array.Count);
            CollectionAssert.AreEqual(scriptHash.ToArray(), array[0].GetSpan().ToArray());
            Assert.AreEqual(notification, array[1].GetString());
        }

        [TestMethod]
        public void TestExecutionEngine_GetScriptContainer()
        {
            GetEngine(true).GetScriptContainer().Should().BeOfType<Transaction>();
        }

        [TestMethod]
        public void TestExecutionEngine_GetCallingScriptHash()
        {
            // Test without

            var engine = GetEngine(true);
            engine.CallingScriptHash.Should().BeNull();

            // Test real

            using ScriptBuilder scriptA = new ScriptBuilder();
            scriptA.Emit(OpCode.DROP); // Drop arguments
            scriptA.Emit(OpCode.DROP); // Drop method
            scriptA.EmitSysCall(ApplicationEngine.System_Runtime_GetCallingScriptHash);

            var contract = new ContractState()
            {
                Manifest = TestUtils.CreateManifest(scriptA.ToArray().ToScriptHash(), "test", ContractParameterType.Any, ContractParameterType.Integer, ContractParameterType.Integer),
                Script = scriptA.ToArray()
            };
            engine = GetEngine(true, true, false);
            engine.Snapshot.Contracts.Add(contract.ScriptHash, contract);

            using ScriptBuilder scriptB = new ScriptBuilder();
            scriptB.EmitAppCall(contract.ScriptHash, "test", 0, 1);
            engine.LoadScript(scriptB.ToArray());

            Assert.AreEqual(VMState.HALT, engine.Execute());

            engine.ResultStack.Pop().GetSpan().ToHexString().Should().Be(scriptB.ToArray().ToScriptHash().ToArray().ToHexString());
        }

        [TestMethod]
        public void TestContract_GetCallFlags()
        {
            GetEngine().GetCallFlags().Should().Be(CallFlags.All);
        }

        [TestMethod]
        public void TestRuntime_Platform()
        {
            GetEngine().GetPlatform().Should().Be("NEO");
        }

        [TestMethod]
        public void TestRuntime_CheckWitness()
        {
            byte[] privateKey = { 0x01,0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
                0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01};
            KeyPair keyPair = new KeyPair(privateKey);
            ECPoint pubkey = keyPair.PublicKey;

            var engine = GetEngine(true);
            ((Transaction)engine.ScriptContainer).Signers[0].Account = Contract.CreateSignatureRedeemScript(pubkey).ToScriptHash();
            ((Transaction)engine.ScriptContainer).Signers[0].Scopes = WitnessScope.CalledByEntry;

            engine.CheckWitness(pubkey.EncodePoint(true)).Should().BeTrue();
            engine.CheckWitness(((Transaction)engine.ScriptContainer).Sender.ToArray()).Should().BeTrue();

            ((Transaction)engine.ScriptContainer).Signers = new Signer[0];
            engine.CheckWitness(pubkey.EncodePoint(true)).Should().BeFalse();

            Action action = () => engine.CheckWitness(new byte[0]);
            action.Should().Throw<ArgumentException>();
        }

        [TestMethod]
        public void TestRuntime_Log()
        {
            var engine = GetEngine(true);
            string message = "hello";
            ApplicationEngine.Log += LogEvent;
            engine.RuntimeLog(Encoding.UTF8.GetBytes(message));
            ((Transaction)engine.ScriptContainer).Script.ToHexString().Should().Be(new byte[] { 0x01, 0x02, 0x03 }.ToHexString());
            ApplicationEngine.Log -= LogEvent;
        }

        [TestMethod]
        public void TestRuntime_GetTime()
        {
            Block block = new Block();
            TestUtils.SetupBlockWithValues(block, UInt256.Zero, out _, out _, out _, out _, out _, out _, 0);
            var engine = GetEngine(true, true);
            engine.Snapshot.PersistingBlock = block;
            engine.GetTime().Should().Be(block.Timestamp);
        }

        [TestMethod]
        public void TestRuntime_Serialize()
        {
            var engine = GetEngine();
            engine.BinarySerialize(100).ToHexString().Should().Be(new byte[] { 0x21, 0x01, 0x64 }.ToHexString());

            //Larger than MaxItemSize
            Assert.ThrowsException<InvalidOperationException>(() => engine.BinarySerialize(new byte[1024 * 1024 * 2]));

            //NotSupportedException
            Assert.ThrowsException<NotSupportedException>(() => engine.BinarySerialize(new InteropInterface(new object())));
        }

        [TestMethod]
        public void TestRuntime_Deserialize()
        {
            var engine = GetEngine();
            engine.BinaryDeserialize(engine.BinarySerialize(100)).GetInteger().Should().Be(100);

            //FormatException
            Assert.ThrowsException<FormatException>(() => engine.BinaryDeserialize(new byte[] { 0xfa, 0x01 }));
        }

        [TestMethod]
        public void TestRuntime_GetInvocationCounter()
        {
            var engine = GetEngine();
            Assert.ThrowsException<InvalidOperationException>(() => engine.GetInvocationCounter());
        }

        [TestMethod]
        public void TestCrypto_Verify()
        {
            var engine = GetEngine(true);
            IVerifiable iv = engine.ScriptContainer;
            byte[] message = iv.GetHashData();
            byte[] privateKey = { 0x01,0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
                0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01};
            KeyPair keyPair = new KeyPair(privateKey);
            ECPoint pubkey = keyPair.PublicKey;
            byte[] signature = Crypto.Sign(message, privateKey, pubkey.EncodePoint(false).Skip(1).ToArray());
            engine.VerifyWithECDsaSecp256r1(message, pubkey.EncodePoint(false), signature).Should().BeTrue();

            byte[] wrongkey = pubkey.EncodePoint(false);
            wrongkey[0] = 5;
            engine.VerifyWithECDsaSecp256r1(new InteropInterface(engine.ScriptContainer), wrongkey, signature).Should().BeFalse();
        }

        [TestMethod]
        public void TestBlockchain_GetHeight()
        {
            GetEngine(true, true).GetBlockchainHeight().Should().Be(0);
        }

        [TestMethod]
        public void TestBlockchain_GetBlock()
        {
            var engine = GetEngine(true, true);

            engine.GetBlock(new byte[] { 0x01 }).Should().BeNull();

            byte[] data1 = new byte[] { 0x01, 0x01, 0x01 ,0x01, 0x01, 0x01, 0x01, 0x01,
                                        0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
                                        0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
                                        0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01};
            engine.GetBlock(data1).Should().BeNull();

            byte[] data2 = new byte[] { 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01 };
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => engine.GetBlock(data2));
        }

        [TestMethod]
        public void TestBlockchain_GetTransaction()
        {
            var engine = GetEngine(true, true);
            byte[] data1 = new byte[] { 0x01, 0x01, 0x01 ,0x01, 0x01, 0x01, 0x01, 0x01,
                                        0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
                                        0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
                                        0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01};
            engine.GetTransaction(new UInt256(data1)).Should().BeNull();
        }

        [TestMethod]
        public void TestBlockchain_GetTransactionHeight()
        {
            var engine = GetEngine(true, true);
            byte[] data1 = new byte[] { 0x01, 0x01, 0x01 ,0x01, 0x01, 0x01, 0x01, 0x01,
                                        0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
                                        0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
                                        0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01};
            engine.GetTransactionHeight(new UInt256(data1)).Should().Be(-1);
        }

        [TestMethod]
        public void TestBlockchain_GetContract()
        {
            var engine = GetEngine(true, true);
            byte[] data1 = new byte[] { 0x01, 0x01, 0x01 ,0x01, 0x01,
                                        0x01, 0x01, 0x01, 0x01, 0x01,
                                        0x01, 0x01, 0x01, 0x01, 0x01,
                                        0x01, 0x01, 0x01, 0x01, 0x01 };
            engine.GetContract(new UInt160(data1)).Should().BeNull();

            var snapshot = Blockchain.Singleton.GetSnapshot();
            var state = TestUtils.GetContract();
            snapshot.Contracts.Add(state.ScriptHash, state);
            engine = ApplicationEngine.Create(TriggerType.Application, null, snapshot);
            engine.LoadScript(new byte[] { 0x01 });
            engine.GetContract(state.ScriptHash).Should().BeSameAs(state);
        }

        [TestMethod]
        public void TestStorage_GetContext()
        {
            var engine = GetEngine(false, true);
            var state = TestUtils.GetContract();
            state.Manifest.Features = ContractFeatures.HasStorage;
            engine.Snapshot.Contracts.Add(state.ScriptHash, state);
            engine.LoadScript(state.Script);
            engine.GetStorageContext().IsReadOnly.Should().BeFalse();
        }

        [TestMethod]
        public void TestStorage_GetReadOnlyContext()
        {
            var engine = GetEngine(false, true);
            var state = TestUtils.GetContract();
            state.Manifest.Features = ContractFeatures.HasStorage;
            engine.Snapshot.Contracts.Add(state.ScriptHash, state);
            engine.LoadScript(state.Script);
            engine.GetReadOnlyContext().IsReadOnly.Should().BeTrue();
        }

        [TestMethod]
        public void TestStorage_Get()
        {
            var snapshot = Blockchain.Singleton.GetSnapshot();
            var state = TestUtils.GetContract();
            state.Manifest.Features = ContractFeatures.HasStorage;

            var storageKey = new StorageKey
            {
                Id = state.Id,
                Key = new byte[] { 0x01 }
            };

            var storageItem = new StorageItem
            {
                Value = new byte[] { 0x01, 0x02, 0x03, 0x04 },
                IsConstant = true
            };
            snapshot.Contracts.Add(state.ScriptHash, state);
            snapshot.Storages.Add(storageKey, storageItem);
            var engine = ApplicationEngine.Create(TriggerType.Application, null, snapshot);
            engine.LoadScript(new byte[] { 0x01 });

            engine.Get(new StorageContext
            {
                Id = state.Id,
                IsReadOnly = false
            }, new byte[] { 0x01 }).ToHexString().Should().Be(storageItem.Value.ToHexString());
        }

        [TestMethod]
        public void TestStorage_Put()
        {
            var engine = GetEngine(false, true);

            //CheckStorageContext fail
            var key = new byte[] { 0x01 };
            var value = new byte[] { 0x02 };
            var state = TestUtils.GetContract();
            var storageContext = new StorageContext
            {
                Id = state.Id,
                IsReadOnly = false
            };
            engine.Put(storageContext, key, value);

            //key.Length > MaxStorageKeySize
            key = new byte[ApplicationEngine.MaxStorageKeySize + 1];
            value = new byte[] { 0x02 };
            Assert.ThrowsException<ArgumentException>(() => engine.Put(storageContext, key, value));

            //value.Length > MaxStorageValueSize
            key = new byte[] { 0x01 };
            value = new byte[ushort.MaxValue + 1];
            Assert.ThrowsException<ArgumentException>(() => engine.Put(storageContext, key, value));

            //context.IsReadOnly
            key = new byte[] { 0x01 };
            value = new byte[] { 0x02 };
            storageContext.IsReadOnly = true;
            Assert.ThrowsException<ArgumentException>(() => engine.Put(storageContext, key, value));

            //storage value is constant
            var snapshot = Blockchain.Singleton.GetSnapshot();
            state.Manifest.Features = ContractFeatures.HasStorage;

            var storageKey = new StorageKey
            {
                Id = state.Id,
                Key = new byte[] { 0x01 }
            };
            var storageItem = new StorageItem
            {
                Value = new byte[] { 0x01, 0x02, 0x03, 0x04 },
                IsConstant = true
            };
            snapshot.Contracts.Add(state.ScriptHash, state);
            snapshot.Storages.Add(storageKey, storageItem);
            engine = ApplicationEngine.Create(TriggerType.Application, null, snapshot);
            engine.LoadScript(new byte[] { 0x01 });
            key = new byte[] { 0x01 };
            value = new byte[] { 0x02 };
            storageContext.IsReadOnly = false;
            Assert.ThrowsException<InvalidOperationException>(() => engine.Put(storageContext, key, value));

            //success
            storageItem.IsConstant = false;
            engine.Put(storageContext, key, value);

            //value length == 0
            key = new byte[] { 0x01 };
            value = new byte[0];
            engine.Put(storageContext, key, value);
        }

        [TestMethod]
        public void TestStorage_PutEx()
        {
            var engine = GetEngine(false, true);
            var snapshot = Blockchain.Singleton.GetSnapshot();
            var state = TestUtils.GetContract();
            state.Manifest.Features = ContractFeatures.HasStorage;
            var storageKey = new StorageKey
            {
                Id = 0x42000000,
                Key = new byte[] { 0x01 }
            };
            var storageItem = new StorageItem
            {
                Value = new byte[] { 0x01, 0x02, 0x03, 0x04 },
                IsConstant = false
            };
            snapshot.Contracts.Add(state.ScriptHash, state);
            snapshot.Storages.Add(storageKey, storageItem);
            engine = ApplicationEngine.Create(TriggerType.Application, null, snapshot);
            engine.LoadScript(new byte[] { 0x01 });
            var key = new byte[] { 0x01 };
            var value = new byte[] { 0x02 };
            var storageContext = new StorageContext
            {
                Id = state.Id,
                IsReadOnly = false
            };
            engine.PutEx(storageContext, key, value, StorageFlags.None);
        }

        [TestMethod]
        public void TestStorage_Delete()
        {
            var engine = GetEngine(false, true);
            var snapshot = Blockchain.Singleton.GetSnapshot();
            var state = TestUtils.GetContract();
            state.Manifest.Features = ContractFeatures.HasStorage;
            var storageKey = new StorageKey
            {
                Id = 0x42000000,
                Key = new byte[] { 0x01 }
            };
            var storageItem = new StorageItem
            {
                Value = new byte[] { 0x01, 0x02, 0x03, 0x04 },
                IsConstant = false
            };
            snapshot.Contracts.Add(state.ScriptHash, state);
            snapshot.Storages.Add(storageKey, storageItem);
            engine = ApplicationEngine.Create(TriggerType.Application, null, snapshot);
            engine.LoadScript(new byte[] { 0x01 });
            state.Manifest.Features = ContractFeatures.HasStorage;
            var key = new byte[] { 0x01 };
            var storageContext = new StorageContext
            {
                Id = state.Id,
                IsReadOnly = false
            };
            engine.Delete(storageContext, key);

            //context is readonly
            storageContext.IsReadOnly = true;
            Assert.ThrowsException<ArgumentException>(() => engine.Delete(storageContext, key));
        }

        [TestMethod]
        public void TestStorageContext_AsReadOnly()
        {
            var engine = GetEngine();
            var state = TestUtils.GetContract();
            var storageContext = new StorageContext
            {
                Id = state.Id,
                IsReadOnly = false
            };
            engine.AsReadOnly(storageContext).IsReadOnly.Should().BeTrue();
        }

        [TestMethod]
        public void TestContract_Call()
        {
            var snapshot = Blockchain.Singleton.GetSnapshot();
            string method = "method";
            var args = new VM.Types.Array { 0, 1 };
            var state = TestUtils.GetContract(method, args.Count);
            state.Manifest.Features = ContractFeatures.HasStorage;

            snapshot.Contracts.Add(state.ScriptHash, state);
            var engine = ApplicationEngine.Create(TriggerType.Application, null, snapshot);
            engine.LoadScript(new byte[] { 0x01 });

            engine.CallContract(state.ScriptHash, method, args);
            engine.CurrentContext.EvaluationStack.Pop().Should().Be(args[0]);
            engine.CurrentContext.EvaluationStack.Pop().Should().Be(args[1]);

            state.Manifest.Permissions[0].Methods = WildcardContainer<string>.Create("a");
            Assert.ThrowsException<InvalidOperationException>(() => engine.CallContract(state.ScriptHash, method, args));

            state.Manifest.Permissions[0].Methods = WildcardContainer<string>.CreateWildcard();
            engine.CallContract(state.ScriptHash, method, args);

            Assert.ThrowsException<InvalidOperationException>(() => engine.CallContract(UInt160.Zero, method, args));
        }

        [TestMethod]
        public void TestContract_CallEx()
        {
            var snapshot = Blockchain.Singleton.GetSnapshot();

            string method = "method";
            var args = new VM.Types.Array { 0, 1 };
            var state = TestUtils.GetContract(method, args.Count);
            state.Manifest.Features = ContractFeatures.HasStorage;
            snapshot.Contracts.Add(state.ScriptHash, state);


            foreach (var flags in new CallFlags[] { CallFlags.None, CallFlags.AllowCall, CallFlags.AllowModifyStates, CallFlags.All })
            {
                var engine = ApplicationEngine.Create(TriggerType.Application, null, snapshot);
                engine.LoadScript(new byte[] { 0x01 });

                engine.CallContractEx(state.ScriptHash, method, args, CallFlags.All);
                engine.CurrentContext.EvaluationStack.Pop().Should().Be(args[0]);
                engine.CurrentContext.EvaluationStack.Pop().Should().Be(args[1]);

                // Contract doesn't exists
                Assert.ThrowsException<InvalidOperationException>(() => engine.CallContractEx(UInt160.Zero, method, args, CallFlags.All));

                // Call with rights
                engine.CallContractEx(state.ScriptHash, method, args, flags);
                engine.CurrentContext.EvaluationStack.Pop().Should().Be(args[0]);
                engine.CurrentContext.EvaluationStack.Pop().Should().Be(args[1]);
            }
        }

        [TestMethod]
        public void TestContract_Destroy()
        {
            var engine = GetEngine(false, true);
            engine.DestroyContract();

            var snapshot = Blockchain.Singleton.GetSnapshot();
            var state = TestUtils.GetContract();
            state.Manifest.Features = ContractFeatures.HasStorage;
            var scriptHash = UInt160.Parse("0xcb9f3b7c6fb1cf2c13a40637c189bdd066a272b4");
            var storageItem = new StorageItem
            {
                Value = new byte[] { 0x01, 0x02, 0x03, 0x04 },
                IsConstant = false
            };

            var storageKey = new StorageKey
            {
                Id = 0x43000000,
                Key = new byte[] { 0x01 }
            };
            snapshot.Contracts.Add(scriptHash, state);
            snapshot.Storages.Add(storageKey, storageItem);
            engine = ApplicationEngine.Create(TriggerType.Application, null, snapshot);
            engine.LoadScript(new byte[0]);
            engine.DestroyContract();
            engine.Snapshot.Storages.Find(BitConverter.GetBytes(0x43000000)).Any().Should().BeFalse();

            //storages are removed
            snapshot = Blockchain.Singleton.GetSnapshot();
            state = TestUtils.GetContract();
            snapshot.Contracts.Add(scriptHash, state);
            engine = ApplicationEngine.Create(TriggerType.Application, null, snapshot);
            engine.LoadScript(new byte[0]);
            engine.DestroyContract();
            engine.Snapshot.Storages.Find(BitConverter.GetBytes(0x43000000)).Any().Should().BeFalse();
        }

        [TestMethod]
        public void TestContract_CreateStandardAccount()
        {
            var engine = GetEngine(true, true);
            ECPoint pubkey = ECPoint.Parse("024b817ef37f2fc3d4a33fe36687e592d9f30fe24b3e28187dc8f12b3b3b2b839e", ECCurve.Secp256r1);
            engine.CreateStandardAccount(pubkey).ToArray().ToHexString().Should().Be("a17e91aff4bb5e0ad54d7ce8de8472e17ce88bf1");
        }

        public static void LogEvent(object sender, LogEventArgs args)
        {
            Transaction tx = (Transaction)args.ScriptContainer;
            tx.Script = new byte[] { 0x01, 0x02, 0x03 };
        }

        private static ApplicationEngine GetEngine(bool hasContainer = false, bool hasSnapshot = false, bool addScript = true)
        {
            var tx = TestUtils.GetTransaction(UInt160.Zero);
            var snapshot = Blockchain.Singleton.GetSnapshot();
            ApplicationEngine engine;
            if (hasContainer && hasSnapshot)
            {
                engine = ApplicationEngine.Create(TriggerType.Application, tx, snapshot);
            }
            else if (hasContainer && !hasSnapshot)
            {
                engine = ApplicationEngine.Create(TriggerType.Application, tx, null);
            }
            else if (!hasContainer && hasSnapshot)
            {
                engine = ApplicationEngine.Create(TriggerType.Application, null, snapshot);
            }
            else
            {
                engine = ApplicationEngine.Create(TriggerType.Application, null, null);
            }
            if (addScript)
            {
                engine.LoadScript(new byte[] { 0x01 });
            }
            return engine;
        }
    }
}
