/**
 * UICombatTextString
 * Author: Denarii Games
 * Version: 1.0
 */

using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace MultiplayerARPG
{
	[RequireComponent(typeof(UIFollowWorldObject))]
	[RequireComponent(typeof(TextWrapper))]
	public class UICombatTextString : UICombatText
	{
		private string text;
		public string Text
		{
			get { return text; }
			set
			{
				text = value;
				CacheText.text = text;
			}
		}
	}
}
