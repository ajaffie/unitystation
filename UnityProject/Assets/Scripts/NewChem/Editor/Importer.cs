﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using YamlDotNet.Serialization;
using Unity.EditorCoroutines.Editor;
using UnityScript.Macros;

namespace Chemistry.Editor
{
	public class Importer : EditorWindow
	{
		Deserializer deserializer = new DeserializerBuilder().Build();
		private string reagentExportPath;
		private string reactionExportPath;
		private string reactionSetExportPath;

		private string reagentsPath;
		private string reactionPath;
		private int progress;
		private int maxProgress;
		private bool cancel;


		[MenuItem("Window/Chemistry Importer")]
		static void ShowWindow() => GetWindow<Importer>("Chemistry Importer");

		void OnGUI()
		{
			ReagentsGui();
			ReactionsGui();

			reagentExportPath = EditorGUILayout.TextField("Reagent export path", reagentExportPath);
			reactionExportPath = EditorGUILayout.TextField("Reaction export path", reactionExportPath);
			reactionSetExportPath = EditorGUILayout.TextField("Reaction set export path", reactionSetExportPath);

			EditorGUI.BeginDisabledGroup(
				!Directory.Exists(reagentsPath) ||
				!Directory.Exists(reactionPath) ||
				!Directory.Exists(reagentExportPath));

			if (GUILayout.Button("Save"))
			{
				this.StartCoroutine(Export());
			}

			if (progress != 0)
			{
				if (EditorUtility.DisplayCancelableProgressBar("Chemistry import", "Importing chemicals...",
					progress / (float) maxProgress))
				{
					cancel = true;
				}
			}
			else
			{
				EditorUtility.ClearProgressBar();
			}

			EditorGUI.EndDisabledGroup();
		}

		private void ReagentsGui()
		{
			EditorGUILayout.BeginHorizontal();
			reagentsPath = EditorGUILayout.TextField("Reagents path", reagentsPath);
			if (Directory.Exists(reagentsPath))
			{
				EditorGUILayout.LabelField($"Contains {Directory.GetFiles(reagentsPath, "*.yml").Length} reagents");
			}
			EditorGUILayout.EndHorizontal();
		}

		private void ReactionsGui()
		{
			EditorGUILayout.BeginHorizontal();
			reactionPath = EditorGUILayout.TextField("Reactions path", reactionPath);
			if (Directory.Exists(reactionPath))
			{
				EditorGUILayout.LabelField($"Contains {Directory.GetFiles(reactionPath, "*.yml").Length} reactions");
			}
			EditorGUILayout.EndHorizontal();
		}

		private IEnumerator Export()
		{
			progress = 0;
			var reagentFiles = Directory.EnumerateFiles(reagentsPath);

			var reagentGroups = reagentFiles
				.Select(file => (file,
					data: deserializer.Deserialize<Dictionary<string, Dictionary<string, object>>>(
						File.ReadAllText(file))))
				.Select(reagents => new Grouping<string, KeyValuePair<string, Reagent>>(reagents.file,
					reagents.data
						.Where(dict => dict.Value.ContainsKey("name"))
						.ToDictionary(reagentData => reagentData.Key, ToReagent)))
				.ToArray();

			var flatReagents = reagentGroups
				.SelectMany(dict => dict)
				.ToDictionary(pair => pair.Key, pair => pair.Value);

			var reactionFiles = Directory.EnumerateFiles(reactionPath)
				.ToArray();

			var reactionSetsData = new Dictionary<string, List<Reaction>>();

			var reactionGroups = reactionFiles
				.Select(file => (file, data: deserializer.Deserialize<Dictionary<string, Dictionary<string, object>>>(File.ReadAllText(file))))
				.Select(reactions => new Grouping<string, KeyValuePair<string, Reaction>>(
					reactions.file,
					reactions.data
						.ToDictionary(r => r.Key, r => ToReaction(r, flatReagents, reactionSetsData))))
				.ToArray();

			maxProgress = flatReagents.Count + reactionGroups.Sum(r => r.Count());

			foreach (var reagentsGroup in reagentGroups)
			{
				var prefix = ToPascalCase(Path.GetFileNameWithoutExtension(reagentsGroup.Key), new char[] {'_'}).Replace("Reagents", "");
				Logger.Log(prefix);
				var prefixPath = Path.Combine(reagentExportPath, prefix);
				if(!Directory.Exists(prefixPath))
				{
					Directory.CreateDirectory(prefixPath);
				}
				foreach (var reagent in reagentsGroup)
				{
					var path = Path.Combine(prefixPath, reagent.Value.Name + ".asset");
					AssetDatabase.CreateAsset(reagent.Value, path);

					progress++;
					if (cancel)
					{
						progress = 0;
						cancel = false;
						yield break;
					}
					yield return new EditorWaitForSeconds(0f);
				}
			}

			foreach (var reactionsGroup in reactionGroups)
			{
				var prefix = ToPascalCase(Path.GetFileNameWithoutExtension(reactionsGroup.Key), new char[] {'_'});
				var prefixPath = Path.Combine(reactionExportPath, prefix);
				Logger.Log(prefix);
				foreach (var reaction in reactionsGroup)
				{
					var path = Path.Combine(
						prefixPath,
						ToPascalCase(reaction.Key.Replace("datum/chemical_reaction/", ""),new char[] {'_', ' '}) +
						".asset");

					try
					{
						if (!Directory.Exists(Path.GetDirectoryName(path)))
						{
							Directory.CreateDirectory(Path.GetDirectoryName(path));
						}

						AssetDatabase.CreateAsset(reaction.Value, path);

					}
					catch (Exception e)
					{

					}

					progress++;
					if (cancel)
					{
						progress = 0;
						cancel = false;
						yield break;
					}
					yield return new EditorWaitForSeconds(0f);
				}
			}

			foreach (var reactionSetData in reactionSetsData)
			{
				var reactionSet = CreateInstance<ReactionSet>();
				reactionSet.reactions = reactionSetData.Value.ToArray();

				var prefix = ToPascalCase(
					Path.GetDirectoryName(reactionSetData.Key
						.Replace("obj/item/", "")),
					new char[] {'_'});

				var prefixPath = Path.Combine(reactionSetExportPath, prefix);

				if(!Directory.Exists(prefixPath))
				{
					Directory.CreateDirectory(prefixPath);
				}

				var path = Path.Combine(
						prefixPath,
						ToPascalCase(Path.GetFileName(reactionSetData.Key), new char[] {'_'}) +
						".asset");

				AssetDatabase.CreateAsset(reactionSet, path);

				if (cancel)
				{
					progress = 0;
					cancel = false;
					yield break;
				}
				yield return new EditorWaitForSeconds(0f);
			}
			progress = 0;
		}

		private static Reaction ToReaction(
			KeyValuePair<string, Dictionary<string, object>> reactionData,
			Dictionary<string, Reagent> reagents,
			Dictionary<string, List<Reaction>> reactionSets)
		{
			var value = reactionData.Value;
			var reaction = CreateInstance<Reaction>();

			if (value.TryGetValue("results", out var resultsData))
			{
				reaction.results = new ReagentMix();
				var results = ((Dictionary<object, object>)resultsData).ToDictionary(
					r => reagents[(string)r.Key],
					r => int.Parse((string)r.Value));

				foreach (var result in results)
				{
					reaction.results.Add(result);
				}
			}

			if (value.TryGetValue("required_reagents", out var ingredientsData))
			{
				reaction.ingredients = new ReagentMix();
				var ingredients = ((Dictionary<object, object>)ingredientsData).ToDictionary(
					r => reagents[(string)r.Key],
					r => int.Parse((string)r.Value));

				foreach (var ingredient in ingredients)
				{
					reaction.ingredients.Add(ingredient);
				}
			}

			if (value.TryGetValue("required_catalysts", out var catalystsData))
			{
				reaction.catalysts = new ReagentMix();
				var catalysts = ((Dictionary<object, object>)catalystsData).ToDictionary(
					r => reagents[(string)r.Key],
					r => int.Parse((string)r.Value));

				foreach (var catalyst in catalysts)
				{
					reaction.catalysts.Add(catalyst);
				}
			}

			if (value.TryGetValue("required_temp", out var temperatureData))
			{
				var temp = int.Parse((string)temperatureData);

				if (value.TryGetValue("is_cold_recipe", out var coldRecipe))
				{
					reaction.tempMax = temp;
				}
				else
				{
					reaction.tempMin = temp;
				}
			}

			if (value.TryGetValue("required_container", out var containerObj))
			{
				var container = (string) containerObj;
				if (!reactionSets.ContainsKey(container))
				{
					reactionSets[container] = new List<Reaction>();
				}
				reactionSets[container].Add(reaction);
			}

			return reaction;
		}

		private static Reagent ToReagent(KeyValuePair<string, Dictionary<string, object>> reagentData)
		{
			var value = reagentData.Value;

			var reagent = CreateInstance<Reagent>();

			if (value.TryGetValue("name", out var name))
			{
				reagent.Name = (string)name;
			}

			if (value.TryGetValue("description", out var description))
			{
				reagent.description = (string)description;
			}

			if (value.TryGetValue("color", out var colorString))
			{
				if (ColorUtility.TryParseHtmlString((string)colorString, out var color))
				{
					reagent.color = new Color(color.r, color.g, color.b, color.a);
				}
			}

			if (value.TryGetValue("reagent_state", out var state))
			{
				switch (state)
				{
					case "SOLID":
						reagent.state = ReagentState.Solid;
						break;
					case "LIQUID":
						reagent.state = ReagentState.Liquid;
						break;
					case "GAS":
						reagent.state = ReagentState.Gas;
						break;
				}
			}

			return reagent;
		}

		public string ToPascalCase(string original, char[] seperator)
		{
			var startsWithLowerCaseChar = new Regex("^[a-z]");

			// replace white spaces with undescore, then replace all invalid chars with empty string
			var pascalCase = original
				// split by underscores
				.Split(seperator, StringSplitOptions.RemoveEmptyEntries)
				// set first letter to uppercase
				.Select(w => startsWithLowerCaseChar.Replace(w, m => m.Value.ToUpper()));

			return string.Concat(pascalCase);
		}

		private class Grouping<TKey, TElement> : IGrouping<TKey, TElement>
		{
			private readonly TKey key;
			private readonly IEnumerable<TElement> values;

			public Grouping(TKey key, IEnumerable<TElement> values)
			{
				this.key = key;
				this.values = values ?? throw new ArgumentNullException(nameof(values));
			}

			public TKey Key => key;

			public IEnumerator<TElement> GetEnumerator()
			{
				return values.GetEnumerator();
			}

			IEnumerator IEnumerable.GetEnumerator()
			{
				return GetEnumerator();
			}
		}
	}
}