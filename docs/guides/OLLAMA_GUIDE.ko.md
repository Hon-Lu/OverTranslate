# Ollama로 로컬 번역 모델 실행하기

> **Language:** **[繁體中文](OLLAMA_GUIDE.md)** ｜ **[English](OLLAMA_GUIDE.en.md)** ｜ **[简体中文](OLLAMA_GUIDE.zh-Hans.md)** ｜ **[日本語](OLLAMA_GUIDE.ja.md)** ｜ **한국어 ✓**

OverTranslate의 **OpenAI** 번역 서비스는 OpenAI API 호환 형식을 지원하므로, [Ollama](https://ollama.com/)와 함께 내 컴퓨터에서 번역 모델을 실행할 수 있습니다. 번역 내용은 컴퓨터 밖으로 나가지 않으며, 외부 번역 API 요금도 들지 않습니다.

이 안내에서는 Ollama와 Hy-MT2 번역 모델을 예로 설명합니다. 아래 단계대로 설치하고 설정하면 바로 사용할 수 있습니다. LM Studio, OpenRouter 같은 다른 OpenAI 호환 서비스도 사용할 수 있습니다(끝부분의 「다른 OpenAI 호환 서비스 사용하기」 참고).

## 권장 사양

이 안내에서 사용하는 모델은 약 **3.1 GB**이고, 불러오면 약 **4.1 GB VRAM**을 사용합니다. **6 GB 이상 VRAM**을 갖춘 그래픽 카드를 권장합니다. 게임 등 GPU를 쓰는 다른 프로그램과 함께 실행한다면 **8 GB 이상 VRAM**을 권장합니다.

실제 사용량은 번역 내용과 다른 프로그램에 따라 달라집니다. VRAM이 부족하면 연산 일부가 CPU로 넘어가 번역이 느려집니다. 이럴 때는 더 작은 버전을 사용하세요(「다른 모델 사용하기」 참고).

## 1. Ollama 설치

[Ollama 공식 사이트](https://ollama.com/download)에서 내려받아 기본 옵션으로 설치합니다. 설치가 끝나면 Ollama를 실행하고, OverTranslate로 번역하는 동안 계속 실행해 두세요.

## 2. 번역 모델 내려받기

Windows의 「명령 프롬프트」 또는 「PowerShell」을 열고 다음 명령을 실행합니다.

```text
ollama pull hf.co/unsloth/Hy-MT2-7B-GGUF:UD-IQ3_XXS
```

내려받기가 끝날 때까지 기다리면 됩니다. 모델 파일은 약 **3.1 GB**이며, 걸리는 시간은 네트워크 속도에 따라 다릅니다.

Hy-MT2는 Tencent가 공개한 번역 전용 모델로, 33개 언어 간 번역을 지원합니다. 여기서는 Unsloth가 다시 패키징한, 크기가 작은 버전을 사용합니다.

## 3. OverTranslate 설정

1. OverTranslate의 **설정**을 엽니다.
2. 번역 서비스에서 **OpenAI**를 선택합니다.
3. **API 주소**와 **API 키**는 비워 둡니다.
4. **모델 설정**은 기본값인 **시스템 기본값**을 그대로 둡니다.

이제 번역을 시작할 수 있습니다. API 주소를 비워 두면 OverTranslate는 Ollama의 로컬 주소 `http://localhost:11434/v1`을 사용하며, 로컬 Ollama에는 API 키가 필요 없습니다. **시스템 기본값**에는 이 안내에서 쓰는 모델, 프롬프트, 고급 매개변수가 이미 들어 있으므로 따로 바꿀 필요가 없습니다.

## 다른 모델 사용하기

### 같은 모델의 다른 크기

[unsloth/Hy-MT2-7B-GGUF](https://huggingface.co/unsloth/Hy-MT2-7B-GGUF)에서 다른 양자화 버전을 고른 뒤, 명령 끝의 버전 이름만 바꾸면 됩니다.

```text
ollama pull hf.co/unsloth/Hy-MT2-7B-GGUF:<버전 이름>
```

파일이 작을수록 필요한 하드웨어 자원은 줄지만, 번역 품질도 떨어질 수 있습니다.

### 다른 모델

[Ollama Models](https://ollama.com/search)나 [Hugging Face](https://huggingface.co/models)에서 찾을 수 있습니다.

Hugging Face의 모델을 사용할 때는 **GGUF** 버전이 제공되는 모델을 고르세요. Safetensors 같은 원본 형식만 있다면, 먼저 변환할 수 있는 모델인지 확인한 뒤 [llama.cpp](https://github.com/ggml-org/llama.cpp)로 GGUF로 변환해야 Ollama에 가져올 수 있습니다.

### 모델 설정 바꾸기

1. OverTranslate의 **모델 설정**에서 **설정 추가**를 선택합니다.
2. 모델 이름을 입력해 저장한 뒤, 새로 만든 설정을 선택합니다.
3. 필요하면 모델 문서를 참고해 프롬프트와 고급 매개변수를 조정합니다.

Hugging Face에서 내려받은 모델은 이름 앞에 `hf.co/`가 있어야 합니다. 예:

```text
hf.co/unsloth/Hy-MT2-7B-GGUF:UD-IQ3_XXS
```

모델 설정마다 모델 이름, 고급 매개변수, 번역 모드, 프롬프트가 함께 저장되며, 설정을 바꾸면 이 항목들도 함께 바뀝니다.

모델마다 맞는 프롬프트와 매개변수가 다를 수 있으니, 먼저 모델 자체의 사용 안내를 확인하세요. OverTranslate의 기본 프롬프트는 인터페이스 언어에 따라 바뀌며, 권장 Hy-MT2 모델에 맞게 조정되어 있습니다.

**시스템 기본값**의 고급 매개변수는 다음과 같습니다.

- Temperature: `0.7`
- Top P: `0.6`
- Seed: `42`

이 값들은 권장 모델의 공식 문서를 따른 것입니다. Seed를 고정하면 같은 설정에서 더 일관된 결과를 얻기 쉽습니다. 모델이 먼저 생각한 뒤 번역문을 출력한다면, 모델 설정의 **고급**에서 **생각 끄기**를 체크하세요. 실시간 번역에는 번역문을 바로 출력하는 모델이 더 적합합니다.

## 다른 OpenAI 호환 서비스 사용하기

LM Studio, OpenRouter 같은 서비스도 OverTranslate와 함께 사용할 수 있습니다. 번역 서비스 설정에 해당 서비스가 제공하는 **API 주소**와 **API 키**를 입력한 뒤, 모델 설정을 추가해 모델 이름을 입력하면 됩니다. 그 밖의 설정 방법은 각 서비스의 문서를 참고하세요.
