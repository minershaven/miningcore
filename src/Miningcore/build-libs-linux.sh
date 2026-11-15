#!/bin/bash

OutDir=../build

export UNAME_S=$(uname -s)
export UNAME_P=$(uname -m || uname -p)

AES=$(../Native/check_cpu.sh aes && echo -maes || echo)
SSE2=$(../Native/check_cpu.sh sse2 && echo -msse2 || echo)
SSE3=$(../Native/check_cpu.sh sse3 && echo -msse3 || echo)
SSSE3=$(../Native/check_cpu.sh ssse3 && echo -mssse3 || echo)
PCLMUL=$(../Native/check_cpu.sh pclmul && echo -mpclmul || echo)
AVX=$(../Native/check_cpu.sh avx && echo -mavx || echo)
AVX2=$(../Native/check_cpu.sh avx2 && echo -mavx2 || echo)
AVX512F=$(../Native/check_cpu.sh avx512f && echo -mavx512f || echo)

export CPU_FLAGS="$AES $SSE2 $SSE3 $SSSE3 $PCLMUL $AVX $AVX2 $AVX512F"

HAVE_AES=$(../Native/check_cpu.sh aes && echo -D__AES__ || echo)
HAVE_SSE2=$(../Native/check_cpu.sh sse2 && echo -DHAVE_SSE2 || echo)
HAVE_SSE3=$(../Native/check_cpu.sh sse3 && echo -DHAVE_SSE3 || echo)
HAVE_SSSE3=$(../Native/check_cpu.sh ssse3 && echo -DHAVE_SSSE3 || echo)
HAVE_PCLMUL=$(../Native/check_cpu.sh pclmul && echo -DHAVE_PCLMUL || echo)
HAVE_AVX=$(../Native/check_cpu.sh avx && echo -DHAVE_AVX || echo)
HAVE_AVX2=$(../Native/check_cpu.sh avx2 && echo -DHAVE_AVX2 || echo)
HAVE_AVX512F=$(../Native/check_cpu.sh avx512f && echo -DHAVE_AVX512F || echo)

export HAVE_FEATURE="$HAVE_AES $HAVE_SSE2 $HAVE_SSE3 $HAVE_SSSE3 $HAVE_PCLMUL $HAVE_AVX $HAVE_AVX2 $HAVE_AVX512F"

(cd ../Native/libmultihash && make clean && make) && mv ../Native/libmultihash/libmultihash.so ../../build
(cd ../Native/libbeamhash && make clean && make) && mv ../Native/libbeamhash/libbeamhash.so ../../build
(cd ../Native/libetchash && make clean && make) && mv ../Native/libetchash/libetchash.so ../../build
(cd ../Native/libethhash && make clean && make) && mv ../Native/libethhash/libethhash.so ../../build
(cd ../Native/libethhashb3 && make -j clean && make -j) && mv ../Native/libethhashb3/libethhashb3.so ../../build
(cd ../Native/libubqhash && make clean && make) && mv ../Native/libubqhash/libubqhash.so ../../build
(cd ../Native/libcryptonote && make clean && make) && mv ../Native/libcryptonote/libcryptonote.so ../../build
(cd ../Native/libcryptonight && make clean && make) && mv ../Native/libcryptonight/libcryptonight.so ../../build
(cd ../Native/libverushash && make clean && make) && mv ../Native/libverushash/libverushash.so ../../build
(cd ../Native/libfiropow && make clean && make) && mv ../Native/libfiropow/libfiropow.so ../../build
(cd ../Native/libkawpow && make clean && make) && mv ../Native/libkawpow/libkawpow.so ../../build
(cd ../Native/libmeowpow && make clean && make) && mv ../Native/libmeowpow/libmeowpow.so ../../build
(cd ../Native/libdero && make clean && make) && mv ../Native/libdero/libdero.so ../../build
(cd ../Native/libcortexcuckoocycle && make clean && make) && mv ../Native/libcortexcuckoocycle/libcortexcuckoocycle.so ../../build
(cd ../Native/libprogpowz && make clean && make) && mv ../Native/libprogpowz/libprogpowz.so ../../build
(cd ../Native/libzanonote && make clean && make) && mv ../Native/libzanonote/libzanonote.so ../../build
(cd ../Native/libmerakipow && make clean && make) && mv ../Native/libmerakipow/libmerakipow.so ../../build
(cd ../Native/libphihash && make clean && make) && mv ../Native/libphihash/libphihash.so ../../build
(cd ../Native/libsccpow && make clean && make) && mv ../Native/libsccpow/libsccpow.so ../../build

((cd /tmp && rm -rf secp256k1 && git clone https://github.com/bitcoin-ABC/secp256k1 && cd secp256k1 && git checkout 04fabb44590c10a19e35f044d11eb5058aac65b2 && mkdir build && cd build && cmake -GNinja .. -DCMAKE_C_FLAGS=-fPIC -DSECP256K1_ENABLE_MODULE_RECOVERY=OFF -DSECP256K1_ENABLE_COVERAGE=OFF -DSECP256K1_ENABLE_MODULE_SCHNORR=ON && ninja) && (cd ../Native/libnexapow && cp /tmp/secp256k1/build/libsecp256k1.a . && make clean && make) && mv ../Native/libnexapow/libnexapow.so ../../build
((cd /tmp && rm -rf RandomX && git clone https://github.com/tevador/RandomX && cd RandomX && git checkout tags/v1.2.1 && mkdir build && cd build && cmake -DARCH=native -DCMAKE_C_FLAGS=-Wa,--noexecstack -DCMAKE_CXX_FLAGS=-Wa,--noexecstack .. && make) && (cd ../Native/librandomx && cp /tmp/RandomX/build/librandomx.a . && make clean && make) && mv ../Native/librandomx/librandomx.so ../../build
((cd /tmp && rm -rf RandomARQ && git clone https://github.com/arqma/RandomARQ && cd RandomARQ && git checkout 3bcb6bafe63d70f8e6f78a0d431e71be2b638083 && mkdir build && cd build && cmake -DARCH=native -DCMAKE_C_FLAGS=-Wa,--noexecstack -DCMAKE_CXX_FLAGS=-Wa,--noexecstack .. && make randomx) && (cd ../Native/librandomarq && cp /tmp/RandomARQ/build/librandomx.a . && make clean && make) && mv ../Native/librandomarq/librandomarq.so ../../build
((cd /tmp && rm -rf Panthera && git clone https://github.com/scala-network/Panthera && cd Panthera && git checkout cc7425f468d935ba328fba5bbb05f8227f4f22d7 && mkdir build && cd build && cmake -DARCH=native -DCMAKE_C_FLAGS=-Wa,--noexecstack -DCMAKE_CXX_FLAGS=-Wa,--noexecstack .. && make randomx) && (cd ../Native/libpanthera && cp /tmp/Panthera/build/librandomx.a . && make clean && make) && mv ../Native/libpanthera/libpanthera.so ../../build
((cd /tmp && rm -rf RandomXSCash && git clone https://github.com/scashnetwork/RandomX RandomXSCash && cd RandomXSCash && git checkout 0b3e0ded68b95491516fe974e3db784ca2742ca7 && mkdir build && cd build && cmake -DARCH=native -DCMAKE_C_FLAGS=-Wa,--noexecstack -DCMAKE_CXX_FLAGS=-Wa,--noexecstack .. && make randomx) && (cd ../Native/librandomxscash && cp /tmp/RandomXSCash/build/librandomx.a . && make clean && make) && mv ../Native/librandomxscash/librandomxscash.so ../../build

# Quantus (QTC) Poseidon2 hash: a Rust crate. Needs cargo (https://rustup.rs);
# skipped with a warning when cargo is not installed, so non-Quantus pools still build.
CARGO=$(command -v cargo || echo "$HOME/.cargo/bin/cargo")
if [ -x "$CARGO" ]; then
  (cd ../Native/libquantus && "$CARGO" build --release) && cp ../Native/libquantus/target/release/libquantus.so ../../build
else
  echo "WARNING: cargo not found, skipping libquantus.so (only needed for Quantus pools). Install Rust from https://rustup.rs"
fi
