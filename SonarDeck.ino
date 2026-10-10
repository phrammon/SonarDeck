/*
 * Credits: www.plusivo.com
 * 
 * Lesson 26: Shift Register
 * 
 * The code below is created for the lesson "Shift Register",
 * where you will learn how to use a shift register to get 
 * more pins for your development board, using the 74HC595
 * Serial to Parallel Converter.
 * 
 * More details can be found in the guide.
 * 
 */

//declare the pins used by the shift register
const int dataPin = D6;//D8;
const int latchPin = D7;
const int clockPin = D8;//D6;
const int btnMinus = D3;
const int btnPlus = D4;
const int btnMute = D5;

//declare two pins used by the remaining LEDs
const int led_9 = D0;
const int led_10 = D1;
const int led_mute = D2;
float chatmix = 50;
int lastBtnMinusState = HIGH;
int lastBtnPlusState = HIGH;
int lastBtnMuteState = HIGH;

byte ledState = 0;
const int a0Threshold = 100;

bool lastA0State = false;


void setup() 
{
  //start the Serial communication at 115200 bits/s
  Serial.begin(115200);

  //set the pins used by the two LEDs to output
  pinMode(D1, OUTPUT);
  pinMode(D2, OUTPUT);
  pinMode(btnMinus, INPUT_PULLUP);
 pinMode(btnPlus, INPUT_PULLUP);
  pinMode(btnMute, INPUT_PULLUP);
  pinMode(D0, OUTPUT);

  //set pins to output so you can control the shift register
  pinMode(latchPin, OUTPUT);
  pinMode(clockPin, OUTPUT);
  pinMode(dataPin, OUTPUT);

  turnOffLeds();
  lightLed(4);
  lightLed(5);
}

void loop() 
{
 int a0Value = analogRead(A0);
    bool a0Pressed = a0Value < a0Threshold;

    // Button was just pressed
    if (!lastA0State && a0Pressed)
    {
        delay(30);  // debounce

        // Check again after debounce
        if (analogRead(A0) < a0Threshold)
        {
            Serial.println("SWITCH_MIC");
            // Do whatever the button should do here
        }
    }

    // Remember state for next loop
    lastA0State = a0Pressed;
if (Serial.available() > 0)
 {
        String command = Serial.readStringUntil('\n');
        command.trim();

        Serial.print("Received: ");
        Serial.println(command);

        if (command.startsWith("ChatMix:"))
        {
            float value = command.substring(8).toFloat();

            Serial.print("ChatMix = ");
            Serial.println(value);

            chatmix = (value + 1) * 50;
            updateLed();
        }
        else if (command == "MUTED:1")
        {
            // show muted state
            
            digitalWrite(led_mute, HIGH);
        }
        else if (command == "MUTED:0")
        {
            // show unmuted state
            
            digitalWrite(led_mute, LOW);
        }
    }

    int minusCurrentState = digitalRead(btnMinus);
    int plusCurrentState = digitalRead(btnPlus);
    int muteCurrentState = digitalRead(btnMute);

if (lastBtnMinusState == HIGH && minusCurrentState == LOW)
{
    delay(30);  // debounce

    if (digitalRead(btnMinus) == LOW)
    {

        Serial.println("CHAT_MINUS");
    }
}

if (lastBtnPlusState == HIGH && plusCurrentState == LOW)
{
    delay(30);  // debounce

    if (digitalRead(btnPlus) == LOW)
    {

        Serial.println("CHAT_PLUS");
    }
}

if (lastBtnMuteState == HIGH && muteCurrentState == LOW)
{
    delay(30);  // debounce

    if (digitalRead(btnMute) == LOW)
    {

        Serial.println("MUTE");
    }
}


// Remember the current state for the next loop
    lastBtnMinusState = minusCurrentState;
    lastBtnPlusState = plusCurrentState;
    lastBtnMuteState = muteCurrentState;
   

}

void updateLed(){
  turnOffLeds();

if (chatmix < 10)
    lightLed(0);
else if (chatmix < 20)
    lightLed(1);
else if (chatmix < 30)
    lightLed(2);
else if (chatmix < 40)
    lightLed(3);
else if (chatmix < 45)
    lightLed(4);
else if (chatmix < 55)
{
    lightLed(4);
    lightLed(5);
}
else if (chatmix < 60)
    lightLed(5);
else if (chatmix < 70)
    lightLed(6);
else if (chatmix < 80)
    lightLed(7);
else if (chatmix < 90)
    lightLed(8);
else
    lightLed(9);
        
        
            Serial.print("Chatmix: ");
        Serial.println(chatmix);
}
void turnOffLeds(){
  Serial.println("Turn off");
  ledState = 0;
     //send a 0 byte value to turn all the LEDs off
  //before any other instruction
  digitalWrite(latchPin, 0);
  shiftOut(dataPin, clockPin, MSBFIRST, 0);
  digitalWrite(latchPin, 1);
  
  digitalWrite(led_9, LOW);
  digitalWrite(led_10, LOW);
}

void lightLed(int i)
{
    Serial.print("light led ");
    Serial.println(i);

    if (i == 8)
    {
        digitalWrite(led_9, HIGH);
    }
    else if (i == 9)
    {
        digitalWrite(led_10, HIGH);
    }
    else
    {
        bitSet(ledState, 7 - i);

        digitalWrite(latchPin, LOW);
        shiftOut(dataPin, clockPin, MSBFIRST, ledState);
        digitalWrite(latchPin, HIGH);
    }
}


