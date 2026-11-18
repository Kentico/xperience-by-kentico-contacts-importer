/**
 * Webpack loader that compiles the imported module as a separate Web Worker bundle and returns
 * its code as a string (`export default "<code>"`).
 *
 * The Xperience admin loads a client module as exactly one file (see `chunkFormat` in
 * webpack.config.js), so the usual `new Worker(new URL(...))` output of a second file does not
 * work. The worker is started from a Blob URL of this string instead (src/worker/create-worker.ts).
 *
 * Usage: import source from './import-worker?inline-worker';
 */
module.exports = function inlineWorkerLoader() {};

module.exports.pitch = function pitch(request) {
  const callback = this.async();
  const { webpack } = this._compiler;
  const filename = "__inline-worker__.js";

  const child = this._compilation.createChildCompiler(
    `inline-worker ${this.resourcePath}`,
    {
      filename,
      chunkFilename: "__inline-worker__.[id].js",
      globalObject: "self",
      // The parent builds a SystemJS library with no chunk format; a worker is a plain script.
      library: undefined,
      libraryTarget: undefined,
      chunkFormat: "array-push",
      chunkLoading: "import-scripts",
    },
    [
      new webpack.webworker.WebWorkerTemplatePlugin(),
      new webpack.EntryPlugin(this.context, `!!${request}`, {
        name: "inline-worker",
      }),
      // A worker cannot load a second file from a Blob URL.
      new webpack.optimize.LimitChunkCountPlugin({ maxChunks: 1 }),
    ],
  );

  child.runAsChild((error, entries, compilation) => {
    // Watch mode (`npm start`): rebuild the worker when any file it imports changes, not only this module.
    if (compilation) {
      for (const file of compilation.fileDependencies) {
        this.addDependency(file);
      }
      for (const directory of compilation.contextDependencies) {
        this.addContextDependency(directory);
      }
      for (const missing of compilation.missingDependencies) {
        this.addMissingDependency(missing);
      }
    }

    if (error) {
      callback(error);

      return;
    }
    if (compilation.errors.length > 0) {
      callback(compilation.errors[0]);

      return;
    }

    const asset = compilation.assets[filename];
    if (!asset) {
      callback(
        new Error("inline-worker-loader: the worker bundle was not produced."),
      );

      return;
    }

    const source = asset.source().toString();
    // Keep the worker out of the output folder: it ships inside the main bundle.
    for (const name of Object.keys(compilation.assets)) {
      delete this._compilation.assets[name];
    }

    callback(null, `export default ${JSON.stringify(source)};`);
  });
};
