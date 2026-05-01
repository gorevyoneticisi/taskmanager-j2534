/* USER CODE BEGIN Header */
/**
  ******************************************************************************
  * @file           : main.c
  * @brief          : Main program body
  ******************************************************************************
  * @attention
  *
  * Copyright (c) 2026 STMicroelectronics.
  * All rights reserved.
  *
  * This software is licensed under terms that can be found in the LICENSE file
  * in the root directory of this software component.
  * If no LICENSE file comes with this software, it is provided AS-IS.
  *
  ******************************************************************************
  */
/* USER CODE END Header */
/* Includes ------------------------------------------------------------------*/
#include "main.h"

/* Private includes ----------------------------------------------------------*/
/* USER CODE BEGIN Includes */

/* USER CODE END Includes */

/* Private typedef -----------------------------------------------------------*/
/* USER CODE BEGIN PTD */

/* USER CODE END PTD */

/* Private define ------------------------------------------------------------*/
/* USER CODE BEGIN PD */

/* USER CODE END PD */

/* Private macro -------------------------------------------------------------*/
/* USER CODE BEGIN PM */

/* USER CODE END PM */

/* Private variables ---------------------------------------------------------*/
CAN_HandleTypeDef hcan1;

UART_HandleTypeDef huart1;

/* USER CODE BEGIN PV */
CAN_TxHeaderTypeDef TxHeader;
CAN_RxHeaderTypeDef RxHeader;
IWDG_HandleTypeDef  hiwdg;
uint32_t TxMailbox;
uint8_t RxData[8];

// UART RX State Machine Variables
uint8_t uart_rx_byte;
uint8_t rx_state = 0;
uint8_t rx_can_id_high = 0;
uint8_t rx_can_id_low  = 0;
uint8_t expected_length = 0;
uint8_t payload_buffer[8];
uint8_t payload_idx = 0;
uint8_t checksum_calc = 0;

// CAN→UART ring buffer — 8 slots of 13 bytes each.
// Prevents buffer corruption when a second CAN frame arrives before
// HAL_UART_Transmit_IT (async) has finished sending the first.
#define TX_RING_SLOTS 8
static uint8_t          uart_tx_ring[TX_RING_SLOTS][13];
static uint8_t          uart_tx_ring_len[TX_RING_SLOTS];
static volatile uint8_t tx_wr   = 0;
static volatile uint8_t tx_rd   = 0;
static volatile uint8_t tx_busy = 0;
/* USER CODE END PV */

/* Private function prototypes -----------------------------------------------*/
void SystemClock_Config(void);
static void MX_GPIO_Init(void);
static void MX_CAN1_Init(void);
static void MX_USART1_UART_Init(void);
/* USER CODE BEGIN PFP */

/* USER CODE END PFP */

/* Private user code ---------------------------------------------------------*/
/* USER CODE BEGIN 0 */

/* USER CODE END 0 */

/**
  * @brief  The application entry point.
  * @retval int
  */
int main(void)
{

  /* USER CODE BEGIN 1 */

  /* USER CODE END 1 */

  /* MCU Configuration--------------------------------------------------------*/

  /* Reset of all peripherals, Initializes the Flash interface and the Systick. */
  HAL_Init();

  /* USER CODE BEGIN Init */

  /* USER CODE END Init */

  /* Configure the system clock */
  SystemClock_Config();

  /* USER CODE BEGIN SysInit */

  /* USER CODE END SysInit */

  /* Initialize all configured peripherals */
  MX_GPIO_Init();
  MX_CAN1_Init();
  MX_USART1_UART_Init();
  /* USER CODE BEGIN 2 */
  // 1. Configure CAN Filter to accept ALL messages (Monitor Mode)
  CAN_FilterTypeDef canfilterconfig;
  canfilterconfig.FilterActivation = CAN_FILTER_ENABLE;
  canfilterconfig.FilterBank = 0;
  canfilterconfig.FilterFIFOAssignment = CAN_RX_FIFO0;
  canfilterconfig.FilterIdHigh = 0x0000;
  canfilterconfig.FilterIdLow = 0x0000;
  canfilterconfig.FilterMaskIdHigh = 0x0000;
  canfilterconfig.FilterMaskIdLow = 0x0000;
  canfilterconfig.FilterMode = CAN_FILTERMODE_IDMASK;
  canfilterconfig.FilterScale = CAN_FILTERSCALE_32BIT;
  HAL_CAN_ConfigFilter(&hcan1, &canfilterconfig);

  // 2. Start the CAN peripheral
  HAL_CAN_Start(&hcan1);

  // 3. Enable CAN RX and error interrupts (bus-off recovery via HAL_CAN_ErrorCallback)
  HAL_CAN_ActivateNotification(&hcan1, CAN_IT_RX_FIFO0_MSG_PENDING
                                     | CAN_IT_ERROR
                                     | CAN_IT_BUSOFF
                                     | CAN_IT_LAST_ERROR_CODE);

  // 4. Start listening to the FT232H via UART (1 byte at a time)
  HAL_UART_Receive_IT(&huart1, &uart_rx_byte, 1);

  // 5. IWDG: ~2 s watchdog (LSI ~32 kHz, prescaler 32 → 1 kHz tick, reload 2000)
  hiwdg.Instance       = IWDG;
  hiwdg.Init.Prescaler = IWDG_PRESCALER_32;
  hiwdg.Init.Reload    = 2000;
  HAL_IWDG_Init(&hiwdg);
  /* USER CODE END 2 */

  /* Infinite loop */
  /* USER CODE BEGIN WHILE */
  while (1)
  {
    /* USER CODE END WHILE */

    /* USER CODE BEGIN 3 */
    HAL_IWDG_Refresh(&hiwdg);
    HAL_Delay(200);
  }
  /* USER CODE END 3 */
}

/**
  * @brief System Clock Configuration
  * @retval None
  */
void SystemClock_Config(void)
{
  RCC_OscInitTypeDef RCC_OscInitStruct = {0};
  RCC_ClkInitTypeDef RCC_ClkInitStruct = {0};

  /** Configure the main internal regulator output voltage
  */
  __HAL_RCC_PWR_CLK_ENABLE();
  __HAL_PWR_VOLTAGESCALING_CONFIG(PWR_REGULATOR_VOLTAGE_SCALE1);

  /** Initializes the RCC Oscillators according to the specified parameters
  * in the RCC_OscInitTypeDef structure.
  */
  RCC_OscInitStruct.OscillatorType = RCC_OSCILLATORTYPE_HSI;
  RCC_OscInitStruct.HSIState = RCC_HSI_ON;
  RCC_OscInitStruct.HSICalibrationValue = RCC_HSICALIBRATION_DEFAULT;
  RCC_OscInitStruct.PLL.PLLState = RCC_PLL_ON;
  RCC_OscInitStruct.PLL.PLLSource = RCC_PLLSOURCE_HSI;
  RCC_OscInitStruct.PLL.PLLM = 8;
  RCC_OscInitStruct.PLL.PLLN = 168;
  RCC_OscInitStruct.PLL.PLLP = RCC_PLLP_DIV2;
  RCC_OscInitStruct.PLL.PLLQ = 4;
  if (HAL_RCC_OscConfig(&RCC_OscInitStruct) != HAL_OK)
  {
    Error_Handler();
  }

  /** Initializes the CPU, AHB and APB buses clocks
  */
  RCC_ClkInitStruct.ClockType = RCC_CLOCKTYPE_HCLK|RCC_CLOCKTYPE_SYSCLK
                              |RCC_CLOCKTYPE_PCLK1|RCC_CLOCKTYPE_PCLK2;
  RCC_ClkInitStruct.SYSCLKSource = RCC_SYSCLKSOURCE_PLLCLK;
  RCC_ClkInitStruct.AHBCLKDivider = RCC_SYSCLK_DIV1;
  RCC_ClkInitStruct.APB1CLKDivider = RCC_HCLK_DIV4;
  RCC_ClkInitStruct.APB2CLKDivider = RCC_HCLK_DIV2;

  if (HAL_RCC_ClockConfig(&RCC_ClkInitStruct, FLASH_LATENCY_5) != HAL_OK)
  {
    Error_Handler();
  }
}

/**
  * @brief CAN1 Initialization Function
  * @param None
  * @retval None
  */
static void MX_CAN1_Init(void)
{

  /* USER CODE BEGIN CAN1_Init 0 */

  /* USER CODE END CAN1_Init 0 */

  /* USER CODE BEGIN CAN1_Init 1 */

  /* USER CODE END CAN1_Init 1 */
  hcan1.Instance = CAN1;
  hcan1.Init.Prescaler = 6;
  hcan1.Init.Mode = CAN_MODE_NORMAL;
  hcan1.Init.SyncJumpWidth = CAN_SJW_1TQ;
  hcan1.Init.TimeSeg1 = CAN_BS1_11TQ;
  hcan1.Init.TimeSeg2 = CAN_BS2_2TQ;
  hcan1.Init.TimeTriggeredMode = DISABLE;
  hcan1.Init.AutoBusOff = DISABLE;
  hcan1.Init.AutoWakeUp = DISABLE;
  hcan1.Init.AutoRetransmission = DISABLE;
  hcan1.Init.ReceiveFifoLocked = DISABLE;
  hcan1.Init.TransmitFifoPriority = DISABLE;
  if (HAL_CAN_Init(&hcan1) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN CAN1_Init 2 */

  /* USER CODE END CAN1_Init 2 */

}

/**
  * @brief USART1 Initialization Function
  * @param None
  * @retval None
  */
static void MX_USART1_UART_Init(void)
{

  /* USER CODE BEGIN USART1_Init 0 */

  /* USER CODE END USART1_Init 0 */

  /* USER CODE BEGIN USART1_Init 1 */

  /* USER CODE END USART1_Init 1 */
  huart1.Instance = USART1;
  huart1.Init.BaudRate = 921600;
  huart1.Init.WordLength = UART_WORDLENGTH_8B;
  huart1.Init.StopBits = UART_STOPBITS_1;
  huart1.Init.Parity = UART_PARITY_NONE;
  huart1.Init.Mode = UART_MODE_TX_RX;
  huart1.Init.HwFlowCtl = UART_HWCONTROL_NONE;
  huart1.Init.OverSampling = UART_OVERSAMPLING_16;
  if (HAL_UART_Init(&huart1) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN USART1_Init 2 */

  /* USER CODE END USART1_Init 2 */

}

/**
  * @brief GPIO Initialization Function
  * @param None
  * @retval None
  */
static void MX_GPIO_Init(void)
{
  /* USER CODE BEGIN MX_GPIO_Init_1 */

  /* USER CODE END MX_GPIO_Init_1 */

  /* GPIO Ports Clock Enable */
  __HAL_RCC_GPIOH_CLK_ENABLE();
  __HAL_RCC_GPIOA_CLK_ENABLE();

  /* USER CODE BEGIN MX_GPIO_Init_2 */

  /* USER CODE END MX_GPIO_Init_2 */
}

/* USER CODE BEGIN 4 */

// Callback: Triggered when the STM32 receives a byte from the FT232H (Windows)
//
// Packet format (PC -> STM32):
//   [0xAA]  Header
//   [0x01]  Command: Transmit a CAN frame
//   [ID_H]  CAN ID high byte (bits 10-8)
//   [ID_L]  CAN ID low byte  (bits 7-0)
//   [LEN]   Data length (0-8 bytes)
//   [D0..n] CAN payload bytes
//   [XOR]   Checksum = ID_H ^ ID_L ^ D0 ^ ... ^ Dn
//
// State machine:
//   0 -> Wait for 0xAA header
//   1 -> Expect 0x01 command byte
//   2 -> Read CAN ID high byte, begin checksum
//   3 -> Read CAN ID low byte,  accumulate checksum
//   4 -> Read payload length
//   5 -> Read payload bytes,    accumulate checksum
//   6 -> Verify checksum and fire CAN message
void HAL_UART_RxCpltCallback(UART_HandleTypeDef *huart)
{
    if (huart->Instance == USART1)
    {
        switch (rx_state)
        {
            case 0: // Wait for Header 0xAA
                if (uart_rx_byte == 0xAA) rx_state = 1;
                break;
            case 1: // Check Command (0x01 = TX CAN)
                if (uart_rx_byte == 0x01) rx_state = 2;
                else rx_state = 0;
                break;
            case 2: // Get CAN ID High byte — start checksum here
                rx_can_id_high = uart_rx_byte;
                checksum_calc  = uart_rx_byte; // XOR accumulator starts with ID_H
                rx_state = 3;
                break;
            case 3: // Get CAN ID Low byte
                rx_can_id_low  = uart_rx_byte;
                checksum_calc ^= uart_rx_byte;
                rx_state = 4;
                break;
            case 4: // Get Length
                expected_length = uart_rx_byte;
                if (expected_length > 8) rx_state = 0;
                else {
                    payload_idx = 0;
                    // checksum_calc retains ID_H ^ ID_L accumulated in cases 2-3
                    if (expected_length == 0) rx_state = 6;
                    else rx_state = 5;
                }
                break;
            case 5: // Read Payload
                payload_buffer[payload_idx++] = uart_rx_byte;
                checksum_calc ^= uart_rx_byte;
                if (payload_idx >= expected_length) rx_state = 6;
                break;
            case 6: // Verify Checksum and Fire CAN Message
                if (uart_rx_byte == checksum_calc)
                {
                    // Reconstruct the CAN ID from the two bytes received from the DLL
                    uint16_t can_id = ((uint16_t)rx_can_id_high << 8) | rx_can_id_low;

                    TxHeader.StdId = can_id;
                    TxHeader.ExtId = 0x00;
                    TxHeader.RTR = CAN_RTR_DATA;
                    TxHeader.IDE = CAN_ID_STD;
                    TxHeader.DLC = expected_length;
                    TxHeader.TransmitGlobalTime = DISABLE;

                    // Blast it to the vehicle
                    HAL_CAN_AddTxMessage(&hcan1, &TxHeader, payload_buffer, &TxMailbox);
                }
                rx_state = 0; // Reset for next frame (whether checksum passed or failed)
                break;
        }
        // Re-arm the UART interrupt to listen for the next byte
        HAL_UART_Receive_IT(&huart1, &uart_rx_byte, 1);
    }
}

// Callback: CAN frame received from vehicle → queue into ring buffer → send to PC
//
// Packet format (STM32 -> PC):
//   [0xBB] [LEN] [ID_H] [ID_L] [D0..Dn] [XOR]
//   XOR = ID_H ^ ID_L ^ D0 ^ ... ^ Dn
//
// Ring buffer (TX_RING_SLOTS slots) prevents overwrite when a second CAN frame
// arrives before HAL_UART_Transmit_IT has finished sending the first.
void HAL_CAN_RxFifo0MsgPendingCallback(CAN_HandleTypeDef *hcan)
{
    if (hcan->Instance == CAN1)
    {
        HAL_CAN_GetRxMessage(hcan, CAN_RX_FIFO0, &RxHeader, RxData);

        uint8_t next_wr = (tx_wr + 1) % TX_RING_SLOTS;
        if (next_wr == tx_rd) return; // ring full — drop frame rather than corrupt

        uint8_t *buf = uart_tx_ring[tx_wr];
        buf[0] = 0xBB;
        buf[1] = RxHeader.DLC;
        buf[2] = (RxHeader.StdId >> 8) & 0xFF;
        buf[3] =  RxHeader.StdId       & 0xFF;

        uint8_t xor_val = buf[2] ^ buf[3];
        for (int i = 0; i < RxHeader.DLC; i++)
        {
            buf[4 + i] = RxData[i];
            xor_val   ^= RxData[i];
        }
        buf[4 + RxHeader.DLC]      = xor_val;
        uart_tx_ring_len[tx_wr]    = 5 + RxHeader.DLC;
        tx_wr = next_wr;

        if (!tx_busy)
        {
            tx_busy = 1;
            HAL_UART_Transmit_IT(&huart1, uart_tx_ring[tx_rd], uart_tx_ring_len[tx_rd]);
        }
    }
}

// Callback: UART TX complete — advance ring buffer and send next frame if queued
void HAL_UART_TxCpltCallback(UART_HandleTypeDef *huart)
{
    if (huart->Instance == USART1)
    {
        tx_rd = (tx_rd + 1) % TX_RING_SLOTS;
        if (tx_rd != tx_wr)
            HAL_UART_Transmit_IT(&huart1, uart_tx_ring[tx_rd], uart_tx_ring_len[tx_rd]);
        else
            tx_busy = 0;
    }
}

// Callback: CAN peripheral error — recover from bus-off by restarting CAN
void HAL_CAN_ErrorCallback(CAN_HandleTypeDef *hcan)
{
    if (hcan->Instance == CAN1)
    {
        if (hcan->ErrorCode & HAL_CAN_ERROR_BOF)
        {
            HAL_CAN_Stop(hcan);
            HAL_CAN_Start(hcan);
            HAL_CAN_ActivateNotification(hcan, CAN_IT_RX_FIFO0_MSG_PENDING
                                             | CAN_IT_ERROR
                                             | CAN_IT_BUSOFF
                                             | CAN_IT_LAST_ERROR_CODE);
        }
    }
}
/* USER CODE END 4 */

/**
  * @brief  This function is executed in case of error occurrence.
  * @retval None
  */
void Error_Handler(void)
{
  /* USER CODE BEGIN Error_Handler_Debug */
  /* User can add his own implementation to report the HAL error return state */
  __disable_irq();
  while (1)
  {
  }
  /* USER CODE END Error_Handler_Debug */
}
#ifdef USE_FULL_ASSERT
/**
  * @brief  Reports the name of the source file and the source line number
  *         where the assert_param error has occurred.
  * @param  file: pointer to the source file name
  * @param  line: assert_param error line source number
  * @retval None
  */
void assert_failed(uint8_t *file, uint32_t line)
{
  /* USER CODE BEGIN 6 */
  /* User can add his own implementation to report the file name and line number,
     ex: printf("Wrong parameters value: file %s on line %d\r\n", file, line) */
  /* USER CODE END 6 */
}
#endif /* USE_FULL_ASSERT */